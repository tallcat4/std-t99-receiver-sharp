using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Text;
namespace SDRSharp.T99;

internal sealed record MapPin(double Latitude,double Longitude,string Label,bool Primary,bool Valid);

// Offline map of Japan: Natural Earth 1:10m (public domain) prefectures, urban areas, lakes, rivers, railroads,
// roads and cities, embedded in the plugin (tools/make-map.py -> japan-map.bin, ~350 kB). No network access.
internal sealed class MapData {
    public enum Geometry : byte {Polygon=1,Line=2,Point=3}
    public sealed class Part {
        public float[] X=[],Y=[],CoarseX=[],CoarseY=[];   // Web Mercator, 0..1
        public float MinX,MinY,MaxX,MaxY;
    }
    public sealed record Feature(int Rank,string Name,float LabelX,float LabelY,Part[] Parts);
    public sealed record Layer(int Id,Geometry Kind,Feature[] Features);
    public Layer[] Layers {get;}
    public int Points {get;}
    public const int Land=0,Urban=1,Lakes=2,Rivers=3,Railroads=4,Roads=5,Places=6;

    public static float MercatorX(double lon)=>(float)((lon+180)/360);
    public static float MercatorY(double lat) {double s=Math.Sin(lat*Math.PI/180);return (float)(0.5-Math.Log((1+s)/(1-s))/(4*Math.PI));}
    private static readonly Lazy<MapData> instance=new(()=>{
        using var stream=typeof(MapData).Assembly.GetManifestResourceStream("japan-map.bin") ?? throw new InvalidOperationException("Map data is missing");
        return new MapData(stream);
    });
    public static MapData Japan=>instance.Value;

    private MapData(Stream compressed) {
        using var reader=new BinaryReader(new GZipStream(compressed,CompressionMode.Decompress),Encoding.UTF8);
        if(Encoding.ASCII.GetString(reader.ReadBytes(4))!="T99M" || reader.ReadByte()!=1) throw new InvalidDataException("Unknown map data");
        int layers=reader.ReadByte(),points=0;
        var list=new List<Layer>();
        for(int l=0;l<layers;l++) {
            int id=reader.ReadByte();var kind=(Geometry)reader.ReadByte();var features=new Feature[reader.ReadUInt32()];
            for(int f=0;f<features.Length;f++) {
                int rank=reader.ReadByte();string name=Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
                float lx=MercatorX(reader.ReadInt32()*1e-5),ly=MercatorY(reader.ReadInt32()*1e-5);
                var parts=new Part[reader.ReadUInt16()];
                for(int p=0;p<parts.Length;p++) {
                    var part=new Part();
                    (part.X,part.Y)=ReadPoints(reader);(part.CoarseX,part.CoarseY)=ReadPoints(reader);
                    part.MinX=part.X.Min();part.MaxX=part.X.Max();part.MinY=part.Y.Min();part.MaxY=part.Y.Max();
                    parts[p]=part;points+=part.X.Length;
                }
                features[f]=new(rank,name,lx,ly,parts);
            }
            list.Add(new(id,kind,features));
        }
        Layers=list.ToArray();Points=points;
    }
    private static (float[] X,float[] Y) ReadPoints(BinaryReader reader) {
        int n=(int)reader.ReadUInt32();var x=new float[n];var y=new float[n];
        for(int i=0;i<n;i++) {x[i]=MercatorX(reader.ReadInt32()*1e-5);y[i]=MercatorY(reader.ReadInt32()*1e-5);}
        return (x,y);
    }
}

// Pan (drag) and zoom (wheel, double click, +/−) over the embedded map; pins for received positions.
// The current transmission's pin is followed unless the map was moved within the last 30 s (⌖ follows again).
internal sealed class MapView : Control {
    public const string Attribution="地図: Natural Earth";
    public const int MinZoom=5,MaxZoom=13,TileSize=256;
    public static bool InJapan(double lat,double lon)=>lat is >= 20 and <= 46 && lon is >= 122 and <= 154;
    private static readonly Color Sea=Color.FromArgb(178,208,224),LandColor=Color.FromArgb(242,239,233),UrbanColor=Color.FromArgb(222,210,196),
        Border=Color.FromArgb(160,150,175),Water=Color.FromArgb(150,190,215),Rail=Color.FromArgb(110,110,110),Expressway=Color.FromArgb(232,150,60),
        RoadColor=Color.FromArgb(246,214,130),Ink=Color.FromArgb(60,64,72);
    private MapPin[] pins=[];
    private double centerX,centerY;      // world pixels at the current zoom
    private int zoom=10;
    private bool follow=true,dragging,enabled=true;
    private Point dragFrom;
    private DateTime lastInteraction=DateTime.MinValue;
    private (double Lat,double Lon)? followed;
    private Rectangle plusBox,minusBox,followBox;
    internal int DrawnParts {get;private set;}

    public MapView() {
        SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        BackColor=Sea;MinimumSize=new(120,180);Cursor=Cursors.Hand;Font=new Font("Yu Gothic UI",8);
        var (x,y)=Project(36.2,138.2,zoom);centerX=x;centerY=y;   // central Japan until a position arrives
    }
    // Web Mercator: world pixel coordinates at zoom z
    public static (double X,double Y) Project(double lat,double lon,int z) {
        double scale=TileSize*Math.Pow(2,z);
        double sin=Math.Sin(lat*Math.PI/180);
        return ((lon+180)/360*scale,(0.5-Math.Log((1+sin)/(1-sin))/(4*Math.PI))*scale);
    }
    public static (double Lat,double Lon) Unproject(double x,double y,int z) {
        double scale=TileSize*Math.Pow(2,z),n=Math.PI-2*Math.PI*y/scale;
        return (180/Math.PI*Math.Atan(Math.Sinh(n)),x/scale*360-180);
    }

    public bool MapEnabled {get=>enabled;set {if(enabled==value) return;enabled=value;Invalidate();}}
    public int ZoomLevel=>zoom;
    public void SetPins(MapPin[] next) {
        pins=next;
        var primary=next.FirstOrDefault(p=>p.Primary&&p.Valid&&InJapan(p.Latitude,p.Longitude)) ?? next.FirstOrDefault(p=>p.Valid&&InJapan(p.Latitude,p.Longitude));
        if(primary!=null && (followed==null || followed.Value.Lat!=primary.Latitude || followed.Value.Lon!=primary.Longitude)) {
            followed=(primary.Latitude,primary.Longitude);
            if(follow || (DateTime.Now-lastInteraction).TotalSeconds>30) {follow=true;CenterOn(primary.Latitude,primary.Longitude);}
        }
        Invalidate();
    }
    private void CenterOn(double lat,double lon) {(centerX,centerY)=Project(lat,lon,zoom);}
    private void Zoom(int delta,Point anchor) {
        int next=Math.Clamp(zoom+delta,MinZoom,MaxZoom);
        if(next==zoom) return;
        double f=Math.Pow(2,next-zoom),ax=anchor.X-Width/2.0,ay=anchor.Y-Height/2.0;
        centerX=(centerX+ax)*f-ax;centerY=(centerY+ay)*f-ay;zoom=next;
        if(follow&&followed!=null) CenterOn(followed.Value.Lat,followed.Value.Lon);
        ClampToJapan();Invalidate();
    }
    private void ClampToJapan() {
        var (x0,y0)=Project(46,122,zoom);var (x1,y1)=Project(20,154,zoom);
        centerX=Math.Clamp(centerX,x0,x1);centerY=Math.Clamp(centerY,y0,y1);
    }
    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);Focus();
        if(plusBox.Contains(e.Location)) {Zoom(1,new(Width/2,Height/2));return;}
        if(minusBox.Contains(e.Location)) {Zoom(-1,new(Width/2,Height/2));return;}
        if(followBox.Contains(e.Location)) {follow=true;if(followed!=null) CenterOn(followed.Value.Lat,followed.Value.Lon);Invalidate();return;}
        if(e.Button==MouseButtons.Left) {dragging=true;dragFrom=e.Location;Cursor=Cursors.SizeAll;}
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        if(!dragging) return;
        centerX-=e.X-dragFrom.X;centerY-=e.Y-dragFrom.Y;dragFrom=e.Location;
        follow=false;lastInteraction=DateTime.Now;ClampToJapan();Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) {base.OnMouseUp(e);dragging=false;Cursor=Cursors.Hand;}
    protected override void OnMouseWheel(MouseEventArgs e) {base.OnMouseWheel(e);Zoom(e.Delta>0?1:-1,follow?new(Width/2,Height/2):e.Location);}
    protected override void OnMouseDoubleClick(MouseEventArgs e) {
        base.OnMouseDoubleClick(e);
        if(!plusBox.Contains(e.Location)&&!minusBox.Contains(e.Location)&&!followBox.Contains(e.Location)) Zoom(1,e.Location);
    }

    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        using var text=new SolidBrush(Ink);
        var primary=pins.FirstOrDefault(p=>p.Primary);
        var shown=pins.Where(p=>p.Valid&&InJapan(p.Latitude,p.Longitude)).ToArray();
        string? message=!enabled?"地図表示はオフです":
            primary!=null&&primary.Valid&&!InJapan(primary.Latitude,primary.Longitude)?"日本国外の位置のため表示しません":
            primary!=null&&!primary.Valid?"測位無効（GPS未測位）":null;
        if(!enabled) {
            g.Clear(Color.FromArgb(25,37,51));
            using var muted=new SolidBrush(Color.FromArgb(145,164,181));
            var size=g.MeasureString(message!,Font);g.DrawString(message,Font,muted,(Width-size.Width)/2,(Height-size.Height)/2);
            plusBox=minusBox=followBox=Rectangle.Empty;return;
        }
        double scale=TileSize*Math.Pow(2,zoom),left=centerX-Width/2.0,top=centerY-Height/2.0;
        DrawMap(g,scale,left,top);
        // pins: other callers as dots, the current transmission as a red pin
        using var label=new Font("Consolas",8,FontStyle.Bold);
        foreach(var p in shown.OrderBy(p=>p.Primary)) {
            var (px,py)=Project(p.Latitude,p.Longitude,zoom);
            float x=(float)(px-left),y=(float)(py-top);
            if(x<-40||y<-40||x>Width+40||y>Height+40) continue;
            if(p.Primary) DrawPin(g,x,y,Color.FromArgb(220,50,60));
            else {
                using var dot=new SolidBrush(Color.FromArgb(230,240,140,40));using var ring=new Pen(Color.White,1.5f);
                g.FillEllipse(dot,x-5,y-5,10,10);g.DrawEllipse(ring,x-5,y-5,10,10);
            }
            if(p.Label.Length>0) {
                var size=g.MeasureString(p.Label,label);float lx=x+8,ly=p.Primary?y-30:y-7;
                using var back=new SolidBrush(Color.FromArgb(210,255,255,255));
                g.FillRectangle(back,lx-2,ly,size.Width+2,size.Height);g.DrawString(p.Label,label,Brushes.Black,lx,ly);
            }
        }
        if(message!=null) {
            var size=g.MeasureString(message,Font);
            using var back=new SolidBrush(Color.FromArgb(220,255,255,255));
            g.FillRectangle(back,6,6,size.Width+6,size.Height+2);g.DrawString(message,Font,text,9,7);
        }
        plusBox=new(Width-30,8,22,22);minusBox=new(Width-30,32,22,22);followBox=new(Width-30,60,22,22);
        using var button=new SolidBrush(Color.FromArgb(235,255,255,255));using var border=new Pen(Color.FromArgb(150,150,150));
        using var glyph=new Font("Segoe UI Symbol",10,FontStyle.Bold);
        foreach(var (box,s) in new[]{(plusBox,"+"),(minusBox,"−"),(followBox,"⌖")}) {
            g.FillRectangle(button,box);g.DrawRectangle(border,box);
            var size=g.MeasureString(s,glyph);
            using var ink=new SolidBrush(box==followBox&&follow?Color.FromArgb(200,40,50):Color.FromArgb(50,50,50));
            g.DrawString(s,glyph,ink,box.X+(box.Width-size.Width)/2,box.Y+(box.Height-size.Height)/2);
        }
        var attribution=$"{Attribution}  ·  z{zoom}";
        var asize=g.MeasureString(attribution,Font);
        using var abg=new SolidBrush(Color.FromArgb(200,255,255,255));
        g.FillRectangle(abg,Width-asize.Width-4,Height-asize.Height-2,asize.Width+4,asize.Height+2);
        g.DrawString(attribution,Font,text,Width-asize.Width-2,Height-asize.Height-1);
    }

    private void DrawMap(Graphics g,double scale,double left,double top) {
        var data=MapData.Japan;
        bool coarse=zoom<9;
        float minX=(float)(left/scale),maxX=(float)((left+Width)/scale),minY=(float)(top/scale),maxY=(float)((top+Height)/scale);
        int drawn=0;
        PointF[] Points(MapData.Part part) {
            var xs=coarse?part.CoarseX:part.X;var ys=coarse?part.CoarseY:part.Y;
            var pts=new PointF[xs.Length];
            for(int i=0;i<xs.Length;i++) pts[i]=new((float)(xs[i]*scale-left),(float)(ys[i]*scale-top));
            return pts;
        }
        bool Visible(MapData.Part p)=>p.MaxX>=minX&&p.MinX<=maxX&&p.MaxY>=minY&&p.MinY<=maxY;
        IEnumerable<PointF[]> Parts(MapData.Layer layer,Func<MapData.Feature,bool>? filter=null) {
            foreach(var f in layer.Features) {
                if(filter!=null&&!filter(f)) continue;
                foreach(var p in f.Parts) if(Visible(p)) {var pts=Points(p);if(pts.Length>=2) {drawn++;yield return pts;}}
            }
        }
        var layers=data.Layers.ToDictionary(l=>l.Id);
        using(var land=new SolidBrush(LandColor)) foreach(var pts in Parts(layers[MapData.Land])) if(pts.Length>=3) g.FillPolygon(land,pts);
        using(var urban=new SolidBrush(UrbanColor)) foreach(var pts in Parts(layers[MapData.Urban])) if(pts.Length>=3) g.FillPolygon(urban,pts);
        using(var water=new SolidBrush(Water)) foreach(var pts in Parts(layers[MapData.Lakes])) if(pts.Length>=3) g.FillPolygon(water,pts);
        using(var border=new Pen(Border,1f) {DashStyle=DashStyle.Dash}) foreach(var pts in Parts(layers[MapData.Land])) g.DrawPolygon(border,pts);
        using(var river=new Pen(Water,zoom>=9?1.5f:1f)) foreach(var pts in Parts(layers[MapData.Rivers])) g.DrawLines(river,pts);
        if(zoom>=7) using(var rail=new Pen(Rail,zoom>=10?1.3f:0.8f) {DashStyle=zoom>=10?DashStyle.Dash:DashStyle.Solid}) foreach(var pts in Parts(layers[MapData.Railroads])) g.DrawLines(rail,pts);
        if(zoom>=8) using(var road=new Pen(RoadColor,zoom>=10?2.2f:1.4f)) foreach(var pts in Parts(layers[MapData.Roads],f=>f.Rank==2)) g.DrawLines(road,pts);
        using(var casing=new Pen(Color.FromArgb(170,110,40),zoom>=10?4f:2.6f)) using(var express=new Pen(Expressway,zoom>=10?2.6f:1.6f))
            foreach(var pts in Parts(layers[MapData.Roads],f=>f.Rank==1)) {g.DrawLines(casing,pts);g.DrawLines(express,pts);}
        // names: prefectures at small zooms, cities by rank
        using var labelBrush=new SolidBrush(Ink);
        using var halo=new SolidBrush(Color.FromArgb(190,255,255,255));
        using var prefectureFont=new Font("Yu Gothic UI",9,FontStyle.Bold);
        void Label(string name,float x,float y,Font font,bool dot) {
            if(x<-50||y<-20||x>Width+50||y>Height+20||name.Length==0) return;
            var size=g.MeasureString(name,font);
            float lx=dot?x+4:x-size.Width/2,ly=dot?y-size.Height/2:y-size.Height/2;
            g.FillRectangle(halo,lx,ly+2,size.Width,size.Height-3);g.DrawString(name,font,labelBrush,lx,ly);
            if(dot) g.FillEllipse(labelBrush,x-2.5f,y-2.5f,5,5);
        }
        if(zoom<=8) foreach(var f in layers[MapData.Land].Features) Label(f.Name,(float)(f.LabelX*scale-left),(float)(f.LabelY*scale-top),prefectureFont,false);
        int maxRank=zoom<=5?2:zoom==6?4:zoom==7?6:99;
        foreach(var f in layers[MapData.Places].Features.Where(f=>f.Rank<=maxRank).OrderByDescending(f=>f.Rank))
            Label(f.Name,(float)(f.LabelX*scale-left),(float)(f.LabelY*scale-top),Font,true);
        DrawnParts=drawn;
    }
    private static void DrawPin(Graphics g,float x,float y,Color color) {
        using var path=new GraphicsPath();
        path.AddArc(x-9,y-30,18,18,150,240);path.AddLine(x+7.8f,y-16.5f,x,y);path.AddLine(x,y,x-7.8f,y-16.5f);path.CloseFigure();
        using var fill=new SolidBrush(color);using var edge=new Pen(Color.White,1.5f);
        g.FillPath(fill,path);g.DrawPath(edge,path);
        g.FillEllipse(Brushes.White,x-3.5f,y-24.5f,7,7);
    }
}
