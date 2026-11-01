using System.Globalization;
namespace SDRSharp.T99;

// Table Annex 2-3-1: CH1-9 from 142.934375 MHz, CH10-18 from 146.934375 MHz, 6.25 kHz steps; CH18 = calling channel.
internal static class Channels {
    public const int Calling=18;
    public static long Frequency(int channel)=>channel<=9?142934375L+(channel-1)*6250L:146934375L+(channel-10)*6250L;
    public static int? Find(long frequency) {
        for(int c=1;c<=18;c++) if(Math.Abs(Frequency(c)-frequency)<=500) return c;
        return null;
    }
}

// Receiving station standby (Annex 2 4.5, Table Annex 2-4-14)
internal enum StandbyMode {Monitor=0,Individual=1,Group=2}

internal static class Names {
    public static string Message(string type)=>type switch {
        "Voice"=>"音声通話",
        "End call"=>"終話",
        "Idle"=>"アイドル",
        "Remote TX request"=>"遠隔送信要求",
        "Location request"=>"位置要求",
        "GPS data 1"=>"位置通知",
        "GPS data 2"=>"GPS時刻",
        "Device ID request"=>"機器ID要求",
        "Caller name"=>"発信者名",
        "Identification"=>"識別符号",
        "Maker format 2"=>"メーカー定義",
        _=>type,
    };
    public static string CallControl(int value)=>value switch {1=>"緊急通信1",2=>"緊急通信2",3=>"緊急応答",_=>""};
}

// What the frames of one transmission said (from its synchronous burst to the next one)
internal sealed record CallInfo {
    public long Number {get;init;}
    public DateTimeOffset Started {get;init;}
    public DateTimeOffset LastFrame {get;init;}
    public string? Caller {get;init;}
    public string? Called {get;init;}
    public uint? Extension {get;init;}
    public int CommType {get;init;}=-1;          // 1 group, 4 individual
    public int Group {get;init;}=-1;             // 0 = simultaneous call number
    public int CallControl {get;init;}=-1;
    public int VoiceMode {get;init;}=-1;
    public string Kind {get;init;}="";           // the message type of PICH2 (what the call is for)
    public string Name {get;init;}="";
    public bool NameFailed {get;init;}
    public bool Ended {get;init;}
    public bool Voice {get;init;}
    public Position? Position {get;init;}
    public GpsTimeInfo? GpsTime {get;init;}
    public string[] Requests {get;init;}=[];      // remote TX / location / device ID requests seen in this call
    public int Frames {get;init;}

    public bool Emergency=>CallControl is 1 or 2;
    public bool Simultaneous=>CommType==1&&Group==0;
    public string CallType=>CommType==4?"個別通話":CommType==1?(Group==0?"一斉通話":Group>0?"グループ通話":"グループ／一斉"):"—";
    public string Destination=>CommType==4?(Called??"—"):CommType==1?(Group==0?"一斉呼出":Group>0?$"グループ {Group:00}":"—"):(Called??"—");
    public string KindText=>Kind.Length>0?Names.Message(Kind):Voice?"音声通話":"—";

    public CallInfo Apply(Frame f,DateTimeOffset now) {
        var next=this with {
            LastFrame=now,Frames=Frames+1,
            Caller=f.Caller??Caller,Called=f.Called??Called,Extension=f.Extension??Extension,
            CommType=f.CommType>=0?f.CommType:CommType,Group=f.Group>=0?f.Group:Group,
            CallControl=f.CallControl>=0?f.CallControl:CallControl,VoiceMode=f.VoiceMode>=0?f.VoiceMode:VoiceMode,
            Name=f.Name.Length>0?f.Name:Name,NameFailed=f.NameFailed,Ended=f.Ended,
            Voice=Voice||f.Halves.Contains(HalfKind.Tch),
            Position=f.Position??Position,GpsTime=f.GpsTime??GpsTime,
        };
        foreach(var m in f.Messages) {
            if(m.Channel=="PICH2") next=next with {Kind=m.Type};
            if(m.Type is "Remote TX request" or "Location request" or "Device ID request" && !next.Requests.Contains(m.Type))
                next=next with {Requests=[..next.Requests,m.Type]};
        }
        return next;
    }
}

internal static class Selective {
    // Device ID "MMM-IIIIIII" (manufacturer 0..127, individual 0..2097151)
    public static string? Normalize(string text) {
        var parts=text.Trim().Split('-');
        if(parts.Length!=2 || !int.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out int maker) ||
           !int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out int id) || maker>127 || id>2097151) return null;
        return $"{maker:000}-{id:0000000}";
    }
    // Table Annex 2-4-14 (output of voice). null = not decided yet (the call type is still unknown).
    public static bool? Accepts(CallInfo call,StandbyMode mode,string ownId,int ownGroup) {
        if(mode==StandbyMode.Monitor) return true;
        if(call.CommType==4) return call.Called==null?null:call.Called==ownId;   // individual call: called number = own device ID
        if(call.CommType!=1 || call.Group<0) return null;
        if(mode==StandbyMode.Individual) return false;                            // group and simultaneous calls (note 2)
        if(call.Group==0) return true;                                            // simultaneous call
        return ownGroup==0 || ownGroup==call.Group;                               // group standby 0 accepts every group
    }
}
