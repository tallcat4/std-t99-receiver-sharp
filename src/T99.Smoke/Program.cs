// SDR# host simulation: the plugin's real panel, worker and native receiver, driven by the
// Annex 2 7.7.2 test signals (build/spec-iq.exe) through IIQProcessor.Process like SDR# does.
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using SDRSharp.Common;
using SDRSharp.Radio;
using SDRSharp.T99;

public class HostProxy : DispatchProxy {
    public Dictionary<string,object?> Values=new() {{"Frequency",146984375L},{"CenterFrequency",146984375L},{"IsPlaying",false},{"AudioIsMuted",false},
        {"DetectorType",DetectorType.AM},{"FilterBandwidth",10000},{"InputSampleRate",2400000.0},{"SourceIsTunable",true},{"CenterFrequencyIsLocked",false}};
    public IIQProcessor? Processor;
    private PropertyChangedEventHandler? changed;
    public void Tune(long value) {Values["Frequency"]=value;changed?.Invoke(this,new("Frequency"));}
    protected override object? Invoke(MethodInfo? m,object?[]? args) {
        string n=m!.Name;
        if(n=="add_PropertyChanged") {changed+=(PropertyChangedEventHandler)args![0]!;return null;}
        if(n=="remove_PropertyChanged") {changed-=(PropertyChangedEventHandler)args![0]!;return null;}
        if(n=="RegisterStreamHook") {Processor=(IIQProcessor)args![0]!;Processor.SampleRate=48000;return null;}
        if(n=="UnregisterStreamHook") {Processor=null;return null;}
        if(n=="StartRadio") {Values["IsPlaying"]=true;changed?.Invoke(this,new("StartRadio"));return null;}
        if(n.StartsWith("set_")) {Values[n[4..]]=args![0];return null;}
        if(n.StartsWith("get_")&&Values.TryGetValue(n[4..],out var v)) return v;
        return m.ReturnType!=typeof(void)&&m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;
    }
}

internal static class Program {
    private static void Check(bool ok,string message) {if(!ok) throw new Exception(message);}
    private static IEnumerable<Control> Walk(Control c) {foreach(Control child in c.Controls) {yield return child;foreach(var sub in Walk(child)) yield return sub;}}
    private static unsafe void Feed(T99Plugin plugin,string path) {
        byte[] bytes=File.ReadAllBytes(path);float[] samples=new float[bytes.Length/4];Buffer.BlockCopy(bytes,0,samples,0,bytes.Length);
        for(int i=0;i<samples.Length;i+=1920) {
            int count=Math.Min(1920,samples.Length-i);
            fixed(float* p=&samples[i]) plugin.Process((Complex*)p,count/2);
            Thread.Sleep(15);Application.DoEvents();   // about real time at 48 kHz (960 samples = 20 ms)
        }
        for(int i=0;i<30;i++) {Thread.Sleep(20);Application.DoEvents();}
    }
    private static void SelectiveCall() {
        CallInfo Individual(string called)=>new() {CommType=4,Called=called,Group=0};
        CallInfo Group(int g)=>new() {CommType=1,Called="000-0000000",Group=g};
        const string me="127-0000654";
        Func<CallInfo,StandbyMode,string,int,bool?> A=Selective.Accepts;
        // Table Annex 2-4-14 and the receiving connection tests (Table Annex 2-7-6)
        Check(A(Individual(me),StandbyMode.Individual,me,0)==true && A(Individual("127-0000001"),StandbyMode.Individual,me,0)==false,"individual standby");
        Check(A(Individual(me),StandbyMode.Group,me,3)==true,"group standby receives own individual calls (note 1)");
        Check(A(Group(3),StandbyMode.Individual,me,3)==false && A(Group(0),StandbyMode.Individual,me,0)==false,"individual standby rejects group/simultaneous (note 2)");
        Check(A(Group(3),StandbyMode.Group,me,3)==true && A(Group(3),StandbyMode.Group,me,5)==false && A(Group(3),StandbyMode.Group,me,0)==true,"group number judgment");
        Check(A(Group(0),StandbyMode.Group,me,5)==true,"simultaneous call");
        Check(A(new CallInfo(),StandbyMode.Group,me,3)==null && A(Group(9),StandbyMode.Monitor,"",0)==true,"undecided / monitor");
        Check(Selective.Normalize("127-654")=="127-0000654" && Selective.Normalize("128-1")==null && Selective.Normalize("x")==null,"device ID format");
        // Map: Web Mercator round trip, Japan bounds, embedded map data
        var (mx,my)=MapView.Project(35.670942,139.750392,17);var (la,lo)=MapView.Unproject(mx,my,17);
        Check(Math.Abs(la-35.670942)<1e-9&&Math.Abs(lo-139.750392)<1e-9&&(int)(mx/256)==116417&&(int)(my/256)==51617,"map projection");
        Check(MapView.InJapan(35.67,139.75)&&MapView.InJapan(24.45,122.93)&&!MapView.InJapan(37.5,127.0-5.1)&&!MapView.InJapan(0,0),"Japan bounds");
        var map=MapData.Japan;
        Check(map.Layers.Length==7&&map.Layers[0].Features.Length==47&&map.Points>50000,$"offline map data: {map.Layers.Length} layers, {map.Points} points");
        Check(map.Layers[MapData.Places].Features.Count(f=>f.Name.EndsWith("市"))>30&&map.Layers[MapData.Land].Features.Any(f=>f.Name=="東京都"),"Japanese names in the map");
        // LO placement: offset below the channel, limited to 40 % of the IQ rate, left alone when not allowed
        Check(T99Plugin.LoCenter(146984375,new Settings(),true,2.4e6)==146884375 && T99Plugin.LoCenter(146984375,new Settings(LoOffsetKHz:500),true,250000)==146884375,"LO offset limit");
        Check(T99Plugin.LoCenter(146984375,new Settings(LoOffset:false),true,2.4e6)==null && T99Plugin.LoCenter(146984375,new Settings(),false,2.4e6)==null && T99Plugin.LoCenter(146984375,new Settings(),true,20000)==null,"LO offset off / fixed / narrow");
        Check(Channels.Find(146984375)==18 && Channels.Find(142934375)==1 && Channels.Find(142984375)==9 && Channels.Find(146934375)==10 && Channels.Find(146000000)==null,"channel table");
    }
    [STAThread]
    private static int Main(string[] args) {
        try {
            string root=Path.GetFullPath(args[0]);string outdir=Path.Combine(root,"build","smoke-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(outdir);
            Environment.SetEnvironmentVariable("T99_SETTINGS_PATH",Path.Combine(outdir,"settings.json"));
            SelectiveCall();
            new Settings(Playback:false).Save();
            var host=DispatchProxy.Create<ISharpControl,HostProxy>();var proxy=(HostProxy)(object)host;
            var plugin=new T99Plugin();plugin.Initialize(host);
            using var form=new Form {ClientSize=new(820,680),Text="STD-T99 Receiver — test host"};
            form.Controls.Add(plugin.Gui);plugin.Gui.Dock=DockStyle.Fill;form.Show();Application.DoEvents();
            var panel=(ReceiverPanel)plugin.Gui;
            panel.Apply(new Settings(Playback:false,Record:true,Folder:outdir));
            var worker=(ReceiverWorker)typeof(T99Plugin).GetField("worker",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(plugin)!;
            worker.Configure(panel.Read());
            // Channel selection tunes the host, and the host frequency selects the channel
            var select=Walk(panel).OfType<ComboBox>().Single(x=>x.Name=="ChannelSelect");
            Check(select.SelectedIndex==17&&select.Items.Count==18,"channel selection follows the initial frequency (CH18)");
            select.SelectedIndex=4;Application.DoEvents();
            Check((long)proxy.Values["Frequency"]! == 142959375L,$"CH5 tuning: {proxy.Values["Frequency"]}");
            Check((long)proxy.Values["CenterFrequency"]! == 142959375L-100000,$"LO offset: centre {proxy.Values["CenterFrequency"]}");
            proxy.Tune(146000000);Application.DoEvents();
            Check(select.SelectedIndex==-1,"a frequency outside the channel table clears the selection");
            select.SelectedIndex=17;Application.DoEvents();
            Check((long)proxy.Values["Frequency"]! == 146984375L&&select.SelectedIndex==17,"CH18 tuning");
            var button=Walk(panel).OfType<Button>().Single(x=>x.Name=="StartReception");button.PerformClick();
            Check(host.IsPlaying&&host.AudioIsMuted&&plugin.Enabled&&host.DetectorType==DetectorType.NFM,"One-button start/mute failed");
            Feed(plugin,Path.Combine(root,"build/spec-48000.cf32"));
            var state=Volatile.Read(ref worker.Snapshot);
            Check(state.Stats.CrcOk==29&&state.Stats.Frames==29,$"IQ -> frame pipeline: {state.Stats.CrcOk}/{state.Stats.Frames}");
            Check(state.History.Length==4,$"call history: {state.History.Length}");
            var calls=state.History.Reverse().ToArray();
            Check(calls.All(c=>c.Caller=="127-0123456"&&c.Position?.LatitudeText=="N35°40.2565′"&&c.Position.LongitudeText=="E139°45.0235′"&&c.Position.Altitude==7),"caller / position");
            Check(calls[0].CallType=="個別通話"&&calls[0].Destination=="127-0000654"&&calls[0].KindText=="音声通話"&&calls[0].Ended,"1-1 individual voice call");
            Check(calls[0].GpsTime?.Text=="2018-07-26 23:51:20 JST"&&calls[0].GpsTime!.Speed01Knot==27&&calls[0].GpsTime!.Direction==267&&calls[0].GpsTime!.Compass=="西","GPS data 2");
            Check(calls[1].Name=="無線試験No.9"&&calls[3].Name=="無線試験No.9","caller name");
            Check(calls[2].Emergency&&calls[2].CallControl==1&&calls[2].KindText=="位置通知"&&calls[2].Simultaneous,"2-1 emergency location notification");
            Check(calls[3].CallType=="グループ通話"&&calls[3].Destination=="グループ 03","6-1 group call");
            Check(state.Drops==0,"Unexpected IQ drops");
            string q=ReceiverPanel.QualityText(state);
            Check(q.StartsWith("CRC 29/29（100%）")&&q.Contains("\n周波数オフセット +")&&!q.Contains("欠落"),"quality line: "+q);
            Check(Walk(panel).OfType<Label>().Single(l=>l.Name=="CallerName").Text=="無線試験No.9","panel caller name");
            Check(Walk(panel).OfType<ListView>().Single().Items.Count==4,"panel history");
            var mapView=Walk(panel).OfType<MapView>().Single();
            mapView.Refresh();
            Check(mapView.DrawnParts>0,"offline map drew nothing around the received position");
            using(var bitmap=new Bitmap(panel.Width,panel.Height)) {panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,panel.Size));bitmap.Save(Path.Combine(outdir,"panel.png"));}
            // CRC count: back to zero after the hold time without frames (shortened here), while IQ keeps coming
            ReceiverWorker.CounterHold=TimeSpan.FromSeconds(0.5);
            var quiet=new float[1920];var rng=new Random(7);
            for(int i=0;i<60;i++) {
                for(int k=0;k<quiet.Length;k++) quiet[k]=(float)(rng.NextDouble()-0.5)*0.06f;
                unsafe {fixed(float* p=quiet) plugin.Process((Complex*)p,quiet.Length/2);}
                Thread.Sleep(20);Application.DoEvents();
            }
            var reset=Volatile.Read(ref worker.Snapshot);
            Check(reset.Stats.Frames==0&&reset.Stats.CrcOk==0&&ReceiverPanel.QualityText(reset).StartsWith("CRC —"),$"CRC count after a quiet period: {reset.Stats.CrcOk}/{reset.Stats.Frames}");
            Check(reset.History.Length==4&&reset.Call?.Name=="無線試験No.9","call information is kept after the count reset");
            ReceiverWorker.CounterHold=TimeSpan.FromSeconds(10);
            button.PerformClick();Application.DoEvents();Thread.Sleep(300);
            Check(!plugin.Enabled&&!host.AudioIsMuted&&host.DetectorType==DetectorType.AM&&host.FilterBandwidth==10000,"Stop/mute restore failed");
            // Recording: one .burst/.llr record per TCH/TCH frame, every frame in .jsonl, decoded audio in .wav
            var burst=Directory.GetFiles(outdir,"*.burst").Single();
            var lines=File.ReadAllLines(Path.ChangeExtension(burst,".jsonl")).Select(l=>JsonDocument.Parse(l).RootElement).ToArray();
            int voice=lines.Count(l=>l.GetProperty("burst").ValueKind==JsonValueKind.Number);
            Check(lines.Length==29&&voice==20,$"jsonl lines {lines.Length}, voice records {voice}");
            Check(lines.All(l=>l.TryGetProperty("utc",out _)&&l.TryGetProperty("halves",out _)),"jsonl fields");
            Check(new FileInfo(burst).Length==voice*41&&new FileInfo(Path.ChangeExtension(burst,".llr")).Length==voice*288,"burst/llr alignment");
            var wav=File.ReadAllBytes(Path.ChangeExtension(burst,".wav"));
            uint data=BitConverter.ToUInt32(wav,40);
            Check(wav.Length==44+data&&data==voice*640*2,$"WAV: {data} bytes for {voice} voice frames");
            plugin.Close();Check(proxy.Processor==null,"Stream hook not removed");
            Console.WriteLine($"PASS: selective call table, host lifecycle, 29/29 frames, 4 calls (ID/name/position/GPS/emergency/group), recording ({voice} bursts, {data/16000.0:F2} s WAV). "+outdir);
            return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
