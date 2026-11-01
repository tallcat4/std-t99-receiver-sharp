using System.Diagnostics;
using System.Globalization;
namespace SDRSharp.T99;

// Two fixed columns:
//   top    : start button and reception status | frequency, channel selection and LO offset; emergency banner below
//   left   : call (caller, name, destination, type, standby result), position with the map, reception settings (standby)
//   right  : eye pattern, reception quality, call history
//   footer : this PC's settings (audio output, recording)
internal sealed class ReceiverPanel : UserControl {
    private static readonly Color Background=Color.FromArgb(17,25,36),CardColor=Color.FromArgb(25,37,51),
        TextColor=Color.FromArgb(229,239,246),Muted=Color.FromArgb(145,164,181),Accent=Color.FromArgb(71,216,179),
        Alert=Color.FromArgb(232,72,85),InputColor=Color.FromArgb(35,50,67);
    private readonly Button start=new() {Text="受信開始",Name="StartReception",Height=40,Width=120,Anchor=AnchorStyles.Right};
    private readonly Label frequency=new() {Text="SDR# 選局チャネル",AutoSize=true};
    private readonly Label channel=new() {Text="—",AutoSize=false,AutoEllipsis=true,Width=170,Height=18};
    private readonly ComboBox channelSelect=new() {DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,Width=176,Name="ChannelSelect",Anchor=AnchorStyles.Left};
    private readonly Label status=new() {Text="●  停止中",AutoSize=true,Anchor=AnchorStyles.Left};
    private readonly Label emergency=new() {Text="",AutoSize=false,Height=30,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,Visible=false};
    private readonly Label caller=new() {Text="—",AutoSize=true,Name="Caller"};
    private readonly Label name=new() {Text="—",AutoSize=true,Name="CallerName"};
    private readonly Label destination=new() {Text="—",AutoSize=true,Name="Destination"};
    private readonly Label detail=Line();
    private readonly Label acceptance=Line();
    private readonly Label latitude=new() {Text="—",AutoSize=true,Name="Latitude"};
    private readonly Label longitude=new() {Text="—",AutoSize=true,Name="Longitude"};
    private readonly Label positionDetail=Line();
    private readonly Label gps=Line();
    private readonly LinkLabel map=new() {Text="ブラウザーで開く",AutoSize=true,Enabled=false};
    private readonly CheckBox mapToggle=new() {Text="地図",Checked=true,AutoSize=true,Name="MapToggle"};
    private readonly MapView mapView=new() {Dock=DockStyle.Fill,Name="Map"};
    private readonly Label quality=Line(2);
    private readonly Label movement=Line();
    private readonly ListView history=new() {View=View.Details,FullRowSelect=true,HeaderStyle=ColumnHeaderStyle.Nonclickable,Height=150,Dock=DockStyle.Fill,
        BorderStyle=BorderStyle.None,Name="History"};
    private readonly EyeView eye=new() {Dock=DockStyle.Fill};
    private readonly ComboBox device=new() {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill,FlatStyle=FlatStyle.Flat};
    private readonly CheckBox playback=new() {Text="音声再生",Checked=true,AutoSize=true,Anchor=AnchorStyles.Left};
    private readonly TrackBar volume=new() {Minimum=0,Maximum=100,Value=80,TickStyle=TickStyle.None,AutoSize=false,Height=26,Dock=DockStyle.Fill};
    private readonly Label volumeValue=new() {Text="80%",AutoSize=true,Anchor=AnchorStyles.Right};
    private readonly ComboBox standby=new() {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill,FlatStyle=FlatStyle.Flat,Name="Standby"};
    private readonly TextBox ownId=new() {Dock=DockStyle.Fill,PlaceholderText="例 127-0000654",BorderStyle=BorderStyle.FixedSingle,Name="OwnId"};
    private readonly NumericUpDown group=new() {Minimum=0,Maximum=63,Dock=DockStyle.Fill,BorderStyle=BorderStyle.FixedSingle,Name="OwnGroup"};
    private readonly CheckBox loOffset=new() {Text="LOオフセット",Checked=true,AutoSize=true,Name="LoOffset"};
    private readonly NumericUpDown loOffsetKHz=new() {Minimum=10,Maximum=1000,Increment=10,Value=100,Width=60,BorderStyle=BorderStyle.FixedSingle,Name="LoOffsetKHz"};
    private readonly CheckBox record=new() {Text="録音する",AutoSize=true,Anchor=AnchorStyles.Left};
    private readonly Button folder=new() {Text="保存先…",AutoSize=true,Anchor=AnchorStyles.Right,FlatStyle=FlatStyle.Flat};
    private readonly Label folderLabel=new() {Text="保存先未選択",AutoEllipsis=true,Height=19,Anchor=AnchorStyles.Left|AnchorStyles.Right};
    private readonly ToolTip tips=new();
    private string destinationFolder="",historyKey="";
    private Position? shownPosition;
    private bool active,applying;
    public event Action<bool>? ActiveChanged;
    public event Action<long>? TuneRequested;   // a channel was chosen: tune SDR# to this frequency (Hz)
    public event Action? SettingsChanged;

    public ReceiverPanel() {
        Font=new Font("Yu Gothic UI",9);BackColor=Background;ForeColor=TextColor;
        AutoScroll=true;MinimumSize=new(320,320);Padding=new(10);
        // The page fills the panel (no horizontal growth); below its minimum size the panel scrolls.
        var page=new TableLayoutPanel {ColumnCount=2,RowCount=3,Dock=DockStyle.Fill,Margin=Padding.Empty,MinimumSize=new(720,560)};
        page.ColumnStyles.Add(new(SizeType.Percent,50));page.ColumnStyles.Add(new(SizeType.Percent,50));
        page.RowStyles.Add(new(SizeType.AutoSize));page.RowStyles.Add(new(SizeType.AutoSize));
        page.RowStyles.Add(new(SizeType.Percent,100));
        AutoScrollMinSize=page.MinimumSize+new Size(Padding.Horizontal,Padding.Vertical+52);   // + footer

        // top, split like the columns below: left = start button and reception status, right = frequency and channel selection
        var control=new TableLayoutPanel {AutoSize=true,ColumnCount=2,RowCount=1,Dock=DockStyle.Fill,Margin=new(0,0,3,6),BackColor=CardColor,Padding=new(10,8,10,8)};
        control.ColumnStyles.Add(new(SizeType.AutoSize));control.ColumnStyles.Add(new(SizeType.Percent,100));
        StyleButton(start,true);start.Anchor=AnchorStyles.Left;start.Margin=new(0,0,14,0);
        var state=new FlowLayoutPanel {AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Anchor=AnchorStyles.Left,Margin=Padding.Empty};
        var title=new Label {Text="STD-T99  デジタル小電力コミュニティ無線",AutoSize=true,Font=new Font(Font.FontFamily,8,FontStyle.Bold),ForeColor=Muted,Margin=new(0,0,0,4)};
        status.ForeColor=Muted;status.Font=new Font(Font.FontFamily,11,FontStyle.Bold);status.Margin=Padding.Empty;
        state.Controls.Add(title);state.Controls.Add(status);
        control.Controls.Add(start,0,0);control.Controls.Add(state,1,0);
        page.Controls.Add(control,0,0);

        var tuningCard=new TableLayoutPanel {AutoSize=true,ColumnCount=2,RowCount=1,Dock=DockStyle.Fill,Margin=new(3,0,0,6),BackColor=CardColor,Padding=new(12,6,10,6)};
        tuningCard.ColumnStyles.Add(new(SizeType.AutoSize));tuningCard.ColumnStyles.Add(new(SizeType.Percent,100));
        var tuning=new FlowLayoutPanel {AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=new(0,0,14,0),Anchor=AnchorStyles.Left};
        frequency.Font=new Font("Segoe UI",15,FontStyle.Bold);frequency.Margin=new(0,4,0,0);
        channel.ForeColor=Accent;channel.Font=new Font(Font.FontFamily,9,FontStyle.Bold);channel.Margin=Padding.Empty;
        tuning.Controls.Add(frequency);tuning.Controls.Add(channel);
        // channel selection (Table Annex 2-3-1): choosing one tunes SDR#
        var select=new FlowLayoutPanel {AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=Padding.Empty,Anchor=AnchorStyles.Right};
        var selectCaption=new Label {Text="チャネル選択",AutoSize=true,ForeColor=Muted,Margin=new(0,0,0,3)};
        for(int c=1;c<=18;c++) channelSelect.Items.Add($"CH{c,2} {Channels.Frequency(c)/1e6:F6}{(c==Channels.Calling?" 呼出":"")}");
        channelSelect.BackColor=InputColor;channelSelect.ForeColor=TextColor;channelSelect.Font=new Font("Consolas",10);channelSelect.Margin=Padding.Empty;
        channelSelect.DropDownHeight=400;
        // under the channel list: the LO (DC spike, IQ image) is kept away from the chosen channel
        var lo=new FlowLayoutPanel {AutoSize=true,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Margin=new(0,4,0,0)};
        loOffset.ForeColor=Muted;loOffset.Margin=new(0,2,4,0);
        loOffsetKHz.BackColor=InputColor;loOffsetKHz.ForeColor=TextColor;loOffsetKHz.Font=new Font("Consolas",9);loOffsetKHz.Margin=new(0,0,4,0);
        var khz=Caption("kHz");khz.Margin=new(0,3,0,0);
        lo.Controls.Add(loOffset);lo.Controls.Add(loOffsetKHz);lo.Controls.Add(khz);
        select.Controls.Add(selectCaption);select.Controls.Add(channelSelect);select.Controls.Add(lo);
        tuningCard.Controls.Add(tuning,0,0);tuningCard.Controls.Add(select,1,0);
        page.Controls.Add(tuningCard,1,0);
        emergency.BackColor=Alert;emergency.ForeColor=Color.White;emergency.Font=new Font(Font.FontFamily,11,FontStyle.Bold);emergency.Margin=new(0,0,0,6);
        page.Controls.Add(emergency,0,1);page.SetColumnSpan(emergency,2);

        // left: call, position (stretches to the bottom)
        var callCard=Card();Add(callCard,Caption("通話"));
        var codes=Column();codes.Margin=new(0,0,0,4);
        name.Font=new Font(Font.FontFamily,11,FontStyle.Bold);destination.Font=new Font(Font.FontFamily,11,FontStyle.Bold);   // Japanese text
        Add(codes,CodeRow(("発信者",caller)));Add(codes,CodeRow(("発信者名",name)));Add(codes,CodeRow(("宛先",destination)));
        Add(callCard,codes);
        Add(callCard,detail);
        acceptance.ForeColor=Muted;Add(callCard,acceptance);
        var positionCard=Card();
        var positionHead=new TableLayoutPanel {AutoSize=true,ColumnCount=3,RowCount=1,Dock=DockStyle.Fill,Margin=Padding.Empty};
        positionHead.ColumnStyles.Add(new(SizeType.Percent,100));positionHead.ColumnStyles.Add(new(SizeType.AutoSize));positionHead.ColumnStyles.Add(new(SizeType.AutoSize));
        map.LinkColor=Accent;map.DisabledLinkColor=Muted;map.Anchor=AnchorStyles.Right;map.Margin=new(8,3,0,0);
        mapToggle.Anchor=AnchorStyles.Right;mapToggle.Margin=new(0,0,0,0);
        positionHead.Controls.Add(Caption("位置情報"),0,0);positionHead.Controls.Add(mapToggle,1,0);positionHead.Controls.Add(map,2,0);
        Add(positionCard,positionHead);
        var position=Column();position.Margin=new(0,0,0,4);
        Add(position,CodeRow(44,("緯度",latitude),("経度",longitude)));Add(positionCard,position);
        positionDetail.ForeColor=Muted;Add(positionCard,positionDetail);gps.ForeColor=Muted;Add(positionCard,gps);movement.ForeColor=Muted;Add(positionCard,movement);
        // the map takes the rest of the card
        for(int r=0;r<positionCard.RowCount;r++) positionCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        positionCard.RowStyles.Add(new RowStyle(SizeType.Percent,100));positionCard.RowCount++;
        mapView.Margin=new(0,4,0,2);positionCard.Controls.Add(mapView,0,positionCard.RowCount-1);positionCard.AutoSize=false;
        var receptionCard=Card();
        Add(receptionCard,Caption("受信設定  ·  待受（選択呼出）"));
        standby.Items.AddRange(["全受信（モニター）","個別待受","グループ待受"]);standby.SelectedIndex=0;standby.BackColor=InputColor;standby.ForeColor=TextColor;
        var idRow=new TableLayoutPanel {AutoSize=true,ColumnCount=5,RowCount=1,Dock=DockStyle.Fill,Margin=Padding.Empty};
        idRow.ColumnStyles.Add(new(SizeType.Percent,58));idRow.ColumnStyles.Add(new(SizeType.AutoSize));idRow.ColumnStyles.Add(new(SizeType.Percent,42));
        idRow.ColumnStyles.Add(new(SizeType.AutoSize));idRow.ColumnStyles.Add(new(SizeType.Absolute,50));
        // small preferred widths: the cells stretch them, and the row must never be wider than the column
        standby.Dock=DockStyle.Fill;standby.Margin=new(0,3,8,0);standby.Width=60;standby.MinimumSize=new(60,0);ownId.Width=50;group.Width=44;
        ownId.BackColor=InputColor;ownId.ForeColor=TextColor;ownId.Font=new Font("Consolas",10);ownId.Margin=new(4,3,8,0);
        group.BackColor=InputColor;group.ForeColor=TextColor;group.Font=new Font("Consolas",10);group.Margin=new(4,3,0,0);
        idRow.Controls.Add(standby,0,0);idRow.Controls.Add(Caption("自局ID"),1,0);idRow.Controls.Add(ownId,2,0);idRow.Controls.Add(Caption("グループ"),3,0);idRow.Controls.Add(group,4,0);
        Add(receptionCard,idRow);
        receptionCard.Margin=Padding.Empty;
        page.Controls.Add(Stack(new(0,0,3,0),(callCard,false),(positionCard,true),(receptionCard,false)),0,2);

        // right: eye, quality, history (stretches to the bottom)
        eye.Height=170;eye.Margin=new(0,0,0,6);eye.Dock=DockStyle.Fill;
        var qualityCard=Card();quality.ForeColor=Muted;Add(qualityCard,quality);
        var historyCard=Card();
        history.BackColor=CardColor;history.ForeColor=TextColor;history.Dock=DockStyle.Fill;history.Margin=new(0,2,0,0);
        foreach(var text in new[]{"時刻","発信者","宛先","種別","発信者名"}) history.Columns.Add(text);
        // Columns share the list's width (no horizontal scroll bar)
        history.ClientSizeChanged+=(_,_)=>{
            float[] share=[.14f,.21f,.19f,.20f,.26f];int width=history.ClientSize.Width,used=0;
            for(int i=0;i<share.Length;i++) {int w=i<share.Length-1?(int)(width*share[i]):Math.Max(40,width-used-1);history.Columns[i].Width=w;used+=w;}
        };
        historyCard.RowStyles.Add(new(SizeType.AutoSize));historyCard.RowStyles.Add(new(SizeType.Percent,100));
        historyCard.Controls.Add(Caption("受信履歴"),0,0);historyCard.Controls.Add(history,0,1);historyCard.AutoSize=false;
        page.Controls.Add(Stack(new(3,0,0,0),(eye,false),(qualityCard,false),(historyCard,true)),1,2);

        // footer (outside the main page, always at the bottom): this PC's settings, one compact frame each
        //   [音声出力 | device | 音声再生 | volume | %]   [録音する | folder | 保存先…]
        var footer=new TableLayoutPanel {AutoSize=true,ColumnCount=2,RowCount=2,Dock=DockStyle.Bottom,Margin=Padding.Empty,Padding=new(0,8,0,0),BackColor=Background};
        footer.ColumnStyles.Add(new(SizeType.Percent,60));footer.ColumnStyles.Add(new(SizeType.Percent,40));
        var rule=new Panel {Height=1,Dock=DockStyle.Fill,BackColor=Color.FromArgb(52,72,92),Margin=new(0,0,0,8)};
        footer.Controls.Add(rule,0,0);footer.SetColumnSpan(rule,2);
        TableLayoutPanel Strip(params (Control Item,SizeType Size,float Width)[] cells) {
            var strip=new TableLayoutPanel {AutoSize=true,ColumnCount=cells.Length,RowCount=1,Dock=DockStyle.Fill,BackColor=CardColor,Padding=new(10,4,8,4)};
            for(int i=0;i<cells.Length;i++) {
                strip.ColumnStyles.Add(new(cells[i].Size,cells[i].Width));
                cells[i].Item.Anchor=cells[i].Size==SizeType.AutoSize?AnchorStyles.Left:AnchorStyles.Left|AnchorStyles.Right;
                if(cells[i].Item.Margin==Padding.Empty||cells[i].Item is CheckBox||cells[i].Item is Label) cells[i].Item.Margin=new(0,3,10,3);
                strip.Controls.Add(cells[i].Item,i,0);
            }
            return strip;
        }
        device.BackColor=InputColor;device.ForeColor=TextColor;device.Dock=DockStyle.None;device.Margin=new(0,3,10,3);
        volume.BackColor=CardColor;volume.Dock=DockStyle.None;volume.Margin=new(0,4,4,0);
        var audio=Strip((Caption("音声出力"),SizeType.AutoSize,0),(device,SizeType.Percent,100),(playback,SizeType.AutoSize,0),
            (volume,SizeType.Absolute,110),(volumeValue,SizeType.AutoSize,0));
        audio.Margin=new(0,0,4,0);
        StyleButton(folder,false);folderLabel.ForeColor=Muted;folder.Margin=new(0,1,0,1);
        var recording=Strip((record,SizeType.AutoSize,0),(folderLabel,SizeType.Percent,100),(folder,SizeType.AutoSize,0));
        recording.Margin=new(4,0,0,0);
        footer.Controls.Add(audio,0,1);footer.Controls.Add(recording,1,1);
        Controls.Add(page);
        Controls.Add(footer);
        page.BringToFront();   // the filled page takes the space the footer leaves

        device.Items.AddRange(WaveOutput.Devices());device.SelectedIndex=0;
        start.Click+=(_,_)=>{
            active=!active;UpdateButton();
            try {ActiveChanged?.Invoke(active);} catch(Exception e) {active=false;UpdateButton();ShowError(e.Message);}
        };
        channelSelect.SelectedIndexChanged+=(_,_)=>{
            if(selectingChannel || channelSelect.SelectedIndex<0) return;
            try {TuneRequested?.Invoke(Channels.Frequency(channelSelect.SelectedIndex+1));} catch(Exception e) {ShowError(e.Message);}
        };
        tips.SetToolTip(channelSelect,"選ぶとSDR#の周波数をそのチャネルに合わせます（CH18 は呼出チャネル）");
        loOffset.CheckedChanged+=(_,_)=>{loOffsetKHz.Enabled=loOffset.Checked;Changed(null,EventArgs.Empty);};
        loOffsetKHz.ValueChanged+=Changed;
        tips.SetToolTip(loOffset,"チャネル選択で選局するとき、SDR#の中心周波数（LO）をチャネルからこの量だけ下げます。\nLOに出る直流成分やIQイメージの影響を避けるためです（IQレートの40%が上限）。");
        device.SelectedIndexChanged+=Changed;playback.CheckedChanged+=Changed;
        volume.ValueChanged+=(_,_)=>{volumeValue.Text=$"{volume.Value}%";Changed(null,EventArgs.Empty);};
        standby.SelectedIndexChanged+=(_,_)=>{UpdateStandbyInputs();Changed(null,EventArgs.Empty);};
        ownId.TextChanged+=(_,_)=>{ownId.ForeColor=ownId.Text.Length==0||Selective.Normalize(ownId.Text)!=null?TextColor:Alert;Changed(null,EventArgs.Empty);};
        group.ValueChanged+=Changed;
        record.CheckedChanged+=(_,_)=>{
            if(!applying&&record.Checked&&string.IsNullOrEmpty(destinationFolder)&&!SelectFolder()) {record.Checked=false;return;}
            Changed(null,EventArgs.Empty);
        };
        folder.Click+=(_,_)=>{if(SelectFolder())Changed(null,EventArgs.Empty);};
        map.LinkClicked+=(_,_)=>OpenMap();
        mapToggle.CheckedChanged+=(_,_)=>{mapView.MapEnabled=mapToggle.Checked;Changed(null,EventArgs.Empty);};
        tips.SetToolTip(record,"WAV、音声フレーム（.burst/.llr）、全フレームの受信情報（.jsonl）を保存します");
        tips.SetToolTip(standby,"全受信: すべての通話を再生します。\n個別待受: 宛先が自局機器IDの個別通話だけを再生します。\nグループ待受: 自局のグループ番号の通話・一斉通話と、自局宛ての個別通話を再生します（グループ番号 0 は全グループ）。");
        tips.SetToolTip(map,"OpenStreetMap をブラウザーで開きます");
        tips.SetToolTip(mapToggle,"発信位置を内蔵の日本地図（Natural Earth）に表示します。ネットワークは使いません。");
        UpdateStandbyInputs();
    }

    // One column of cards; the `fill` items take the remaining height (their content stays at the top)
    private static TableLayoutPanel Stack(Padding margin,params (Control Item,bool Fill)[] items) {
        var stack=new TableLayoutPanel {ColumnCount=1,RowCount=items.Length,Dock=DockStyle.Fill,Margin=margin,Padding=Padding.Empty};
        for(int i=0;i<items.Length;i++) {
            var (item,fill)=items[i];
            stack.RowStyles.Add(fill?new RowStyle(SizeType.Percent,100):new RowStyle(SizeType.AutoSize));
            if(fill && item is TableLayoutPanel card && card.AutoSize) {
                for(int r=0;r<card.RowCount;r++) card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                card.RowStyles.Add(new RowStyle(SizeType.Percent,100));card.RowCount++;
                card.Controls.Add(new Panel {Dock=DockStyle.Fill,Margin=Padding.Empty},0,card.RowCount-1);
            }
            if(fill && item is TableLayoutPanel t) t.AutoSize=false;
            item.Dock=DockStyle.Fill;
            stack.Controls.Add(item,0,i);
        }
        return stack;
    }
    private static TableLayoutPanel Column()=>new() {AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,Dock=DockStyle.Fill,Margin=Padding.Empty};
    private static TableLayoutPanel Card() {var card=Column();card.BackColor=CardColor;card.Padding=new(10,6,10,6);card.Margin=new(0,0,0,6);return card;}
    private static TableLayoutPanel Row() {
        var row=new TableLayoutPanel {AutoSize=true,ColumnCount=2,RowCount=1,Dock=DockStyle.Fill,Margin=Padding.Empty};
        row.ColumnStyles.Add(new(SizeType.Percent,50));row.ColumnStyles.Add(new(SizeType.Percent,50));return row;
    }
    // A text line that keeps its height and is cut with "…" instead of widening the column
    private static Label Line(int lines=1)=>new() {Text="—",AutoSize=false,AutoEllipsis=true,Height=19*lines,Dock=DockStyle.Fill};
    private static Label Caption(string value)=>new() {Text=value,AutoSize=true,ForeColor=Muted,Anchor=AnchorStyles.Left};
    // Caption | value cells; values keep a font set before (the caller name), otherwise Consolas
    private static TableLayoutPanel CodeRow(params (string Caption,Label Value)[] fields)=>CodeRow(70,fields);
    private static TableLayoutPanel CodeRow(int captionWidth,params (string Caption,Label Value)[] fields) {
        var row=new TableLayoutPanel {AutoSize=true,ColumnCount=fields.Length*2,RowCount=1,Dock=DockStyle.Fill,Margin=Padding.Empty,CellBorderStyle=TableLayoutPanelCellBorderStyle.Single};
        int column=0;
        foreach(var (caption,value) in fields) {
            row.ColumnStyles.Add(new(SizeType.Absolute,captionWidth));row.ColumnStyles.Add(new(SizeType.Percent,100f/fields.Length));
            var label=Caption(caption);label.Margin=new(8,5,4,5);
            if(value.Font.Size<10) value.Font=new Font("Consolas",11,FontStyle.Bold);
            value.Anchor=AnchorStyles.Left|AnchorStyles.Right;value.TextAlign=ContentAlignment.MiddleRight;value.Margin=new(4,5,8,5);
            row.Controls.Add(label,column++,0);row.Controls.Add(value,column++,0);
        }
        return row;
    }
    private static void Add(TableLayoutPanel parent,Control control) {
        if(control is not TableLayoutPanel && control is not EyeView)control.Margin=new Padding(0,2,0,2);
        if(parent.ColumnCount==1) {control.Anchor=AnchorStyles.Left|AnchorStyles.Right;parent.RowCount++;}
        parent.Controls.Add(control);
    }
    private static void StyleButton(Button button,bool primary) {
        button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderSize=primary?0:1;
        button.FlatAppearance.BorderColor=Color.FromArgb(65,87,106);
        button.BackColor=primary?Accent:CardColor;button.ForeColor=primary?Background:TextColor;
        button.Font=new Font("Yu Gothic UI",primary?10:9,primary?FontStyle.Bold:FontStyle.Regular);
        button.Cursor=Cursors.Hand;
    }
    private void UpdateButton() {start.Text=active?"受信停止":"受信開始";start.BackColor=active?Color.FromArgb(39,65,75):Accent;start.ForeColor=active?TextColor:Background;}
    private void UpdateStandbyInputs() {ownId.Enabled=standby.SelectedIndex!=0;group.Enabled=standby.SelectedIndex==2;}
    private void Changed(object? sender,EventArgs e) {if(!applying)SettingsChanged?.Invoke();}
    private bool SelectFolder() {
        using var dialog=new FolderBrowserDialog {Description="録音先フォルダー",SelectedPath=destinationFolder,UseDescriptionForTitle=true};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return false;
        destinationFolder=dialog.SelectedPath;ShowFolder();return true;
    }
    private void ShowFolder() {folderLabel.Text=string.IsNullOrEmpty(destinationFolder)?"保存先未選択":destinationFolder;tips.SetToolTip(folderLabel,destinationFolder);}
    private void OpenMap() {
        if(shownPosition==null)return;
        string lat=shownPosition.Latitude.ToString("F6",CultureInfo.InvariantCulture),lon=shownPosition.Longitude.ToString("F6",CultureInfo.InvariantCulture);
        try {Process.Start(new ProcessStartInfo($"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=16/{lat}/{lon}") {UseShellExecute=true});}
        catch(Exception e) {ShowError(e.Message);}
    }

    public void Apply(Settings settings) {
        applying=true;try {
            device.SelectedIndex=Math.Clamp(settings.Device+1,0,device.Items.Count-1);playback.Checked=settings.Playback;
            volume.Value=Math.Clamp(settings.Volume,0,100);record.Checked=settings.Record;destinationFolder=settings.Folder;ShowFolder();
            standby.SelectedIndex=(int)settings.Standby;ownId.Text=settings.OwnId;group.Value=Math.Clamp(settings.Group,0,63);UpdateStandbyInputs();
            mapToggle.Checked=settings.Map;mapView.MapEnabled=settings.Map;
            loOffset.Checked=settings.LoOffset;loOffsetKHz.Value=Math.Clamp(settings.LoOffsetKHz,10,1000);loOffsetKHz.Enabled=settings.LoOffset;
        } finally {applying=false;}
    }
    public Settings Read()=>new(device.SelectedIndex-1,playback.Checked,volume.Value,record.Checked,destinationFolder,
        (StandbyMode)Math.Max(0,standby.SelectedIndex),Selective.Normalize(ownId.Text)??"",(int)group.Value,mapToggle.Checked,loOffset.Checked,(int)loOffsetKHz.Value);
    private bool selectingChannel;
    public void SetFrequency(long value,long center) {
        frequency.Text=$"{value/1e6:F5} MHz";
        int? c=Channels.Find(value);
        channel.Text=c==null?"STD-T99 のチャネルではありません":c==Channels.Calling?$"CH{c}  呼出チャネル":$"CH{c}";
        channel.ForeColor=c==null?Muted:Accent;
        // A channel at the LO sits on the tuner's DC spike and IQ image
        if(center!=0 && Math.Abs(value-center)<10000) {channel.Text+="  ·  LO付近（受信が悪化することがあります）";channel.ForeColor=Color.Salmon;}
        selectingChannel=true;
        try {channelSelect.SelectedIndex=c==null?-1:c.Value-1;} finally {selectingChannel=false;}
    }
    public void ShowError(string text) {status.Text=text;status.ForeColor=Color.Salmon;}
    public void RefreshEye(IEnumerable<EyeBatch> batches,long epoch,bool running)=>eye.Advance(batches,epoch,running);

    public void RefreshState(Snapshot s,bool running,bool source,Settings settings) {
        active=running;UpdateButton();
        status.Text="●  "+(running&&!source?"SDR#の受信開始を待機中":s.Status);
        status.ForeColor=running&&source&&s.Stats.Locked!=0?Accent:Muted;
        var c=s.Call;
        emergency.Visible=c!=null&&c.CallControl is 1 or 2 or 3;
        if(c!=null&&emergency.Visible) {
            emergency.Text=$"{Names.CallControl(c.CallControl)}{(s.CallActive?"  受信中":"")}";
            emergency.BackColor=c.Emergency?Alert:Color.FromArgb(196,120,40);
        }
        caller.Text=c?.Caller??"—";
        name.Text=c==null?"—":c.Name.Length>0?c.Name:c.NameFailed?"（検査不一致）":"—";
        destination.Text=c?.Destination??"—";
        if(c==null) {detail.Text="—";acceptance.Text="";}
        else {
            var parts=new List<string>{c.CallType,c.KindText};
            if(c.VoiceMode>=0) parts.Add(c.VoiceMode==0?"TOKUDER":"音声方式 未定義");
            parts.Add(s.CallActive?"受信中":c.Ended?"終話":"—");
            if(c.Requests.Length>0) parts.Add("要求: "+string.Join("・",c.Requests.Select(Names.Message)));
            detail.Text=string.Join("  ·  ",parts);
            acceptance.Text=settings.Standby==StandbyMode.Monitor?"待受: 全受信（すべて再生）":
                s.Accepted==true?"着信: 自局宛て（再生）":s.Accepted==false?"着信対象外（再生しません）":"着信判定待ち";
            acceptance.ForeColor=s.Accepted==false&&settings.Standby!=StandbyMode.Monitor?Color.Salmon:Muted;
            // Without an own device ID no individual call can match (Table Annex 2-4-14)
            if(settings.Standby!=StandbyMode.Monitor && settings.OwnId.Length==0) {acceptance.Text+="  ·  自局機器IDが未設定です";acceptance.ForeColor=Color.Salmon;}
        }
        var p=c?.Position;shownPosition=p;map.Enabled=p!=null&&p.Valid;
        RefreshMap(c,s.History);
        latitude.Text=p?.LatitudeText??"—";longitude.Text=p?.LongitudeText??"—";
        positionDetail.Text=p==null?"—":$"高度 {p.Altitude} m  ·  測位 {(p.Valid?"有効":"無効 (V)")}  ·  {p.Latitude.ToString("F6",CultureInfo.InvariantCulture)}, {p.Longitude.ToString("F6",CultureInfo.InvariantCulture)}";
        var g=c?.GpsTime;
        // GPS data 2 (4.2.4.7, 4.8): speed and direction of movement of the transmitting station itself
        gps.Text=g==null?"GPS時刻 —":$"GPS時刻 {g.Text}";
        movement.Text=g==null?"移動 —":$"移動 {g.Speed01Knot*0.1852:F1} km/h（{g.Speed01Knot/10.0:F1} kn）  ·  進行方向 {g.Direction}°（{g.Compass}）";
        quality.Text=QualityText(s);
        RefreshHistory(s.History);
    }
    // Current transmission as the primary pin, the last position of every other caller in the history as dots
    private string mapKey="";
    private void RefreshMap(CallInfo? current,CallInfo[] calls) {
        var pins=new List<MapPin>();
        if(current?.Position is {} cp) pins.Add(new(cp.Latitude,cp.Longitude,current.Name.Length>0?current.Name:current.Caller??"",true,cp.Valid));
        var seen=new HashSet<string>();if(current?.Caller!=null) seen.Add(current.Caller);
        foreach(var h in calls) {
            if(h.Position is not {Valid:true} hp || h.Number==current?.Number) continue;
            if(!seen.Add(h.Caller??$"#{h.Number}")) continue;
            pins.Add(new(hp.Latitude,hp.Longitude,h.Caller??"",false,true));
        }
        string key=string.Join("|",pins.Select(x=>$"{x.Latitude:F6},{x.Longitude:F6},{x.Label},{x.Primary},{x.Valid}"));
        if(key==mapKey) return;
        mapKey=key;mapView.SetPins(pins.ToArray());
    }
    // "CRC 29/29（100%）  ·  信号 −36.6 dB" / "周波数オフセット +121 Hz" (+ "欠落 IQ n／音声 m" when there are drops)
    internal static string QualityText(Snapshot s) {
        static string Signed(double v,string format)=>(v<0?"−":v>0?"+":"±")+Math.Abs(v).ToString(format,CultureInfo.InvariantCulture);
        string crc=s.Stats.Frames==0?"CRC —":$"CRC {s.Stats.CrcOk}/{s.Stats.Frames}（{100.0*s.Stats.CrcOk/s.Stats.Frames:F0}%）";
        string power=s.Stats.Samples==0?"信号 —":$"信号 {(s.Stats.Power<0?"−":"")}{Math.Abs(s.Stats.Power).ToString("F1",CultureInfo.InvariantCulture)} dB";
        string offset=s.Stats.Frames==0?"周波数オフセット —":$"周波数オフセット {Signed(Math.Round(s.Stats.FrequencyError),"F0")} Hz";
        string drops=s.Drops==0&&s.AudioDrops==0?"":$"  ·  欠落 IQ {s.Drops}／音声 {s.AudioDrops}";
        return $"{crc}  ·  {power}\n{offset}{drops}";
    }
    private void RefreshHistory(CallInfo[] calls) {
        string key=string.Join(",",calls.Take(3).Select(c=>$"{c.Number}:{c.Frames}"))+"/"+calls.Length;
        if(key==historyKey) return;
        historyKey=key;
        history.BeginUpdate();
        try {
            history.Items.Clear();
            foreach(var c in calls) {
                var item=new ListViewItem([c.Started.ToString("HH:mm:ss"),c.Caller??"—",c.Destination,
                    c.Emergency?Names.CallControl(c.CallControl):c.KindText,c.Name]);
                if(c.Emergency) item.ForeColor=Alert;
                history.Items.Add(item);
            }
        } finally {history.EndUpdate();}
    }
    protected override void Dispose(bool disposing) {if(disposing)tips.Dispose();base.Dispose(disposing);}
}
