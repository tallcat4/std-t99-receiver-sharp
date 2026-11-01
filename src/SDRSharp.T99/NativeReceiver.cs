using System.Runtime.InteropServices;
using System.Text;
namespace SDRSharp.T99;

[StructLayout(LayoutKind.Sequential)]
internal struct ReceiverStats {
    public ulong Samples, Frames, CrcOk;
    public double Power, FrequencyError;
    public int Locked, Active;
}

// native/t99.h t99_frame
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeFrame {
    public double Seconds;
    public ulong Sample;
    public int Kind, SyncOk, Crc, Voice;
    public fixed int Half[2];
    public fixed int HalfCrc[2];
    public int NewCall, HasCaller, HasCalled, HasIdentification;
    public fixed byte Caller[16];
    public fixed byte Called[16];
    public uint Extension;
    public int CommType, Group, CallControl, VoiceMode, LastType, Ended, HasPosition;
    public uint LatRaw, LonRaw;
    public double Lat, Lon;
    public int Altitude, GpsValid, HasGpsTime;
    public uint GpsTime, GpsDate;
    public int Speed, Direction, NameFailed;
    public fixed byte Name[64];
    public fixed byte Messages[256];
    public fixed byte Payload[36];
    public fixed byte Llr[288];

    private static string Text(byte* p,int capacity) {
        int n=0;while(n<capacity&&p[n]!=0)n++;
        return Encoding.UTF8.GetString(p,n);
    }
    public Frame Copy() {
        fixed(byte* caller=Caller,called=Called,name=Name,messages=Messages,payload=Payload,llr=Llr)
        fixed(int* half=Half,halfCrc=HalfCrc) {
            Position? position=HasPosition!=0?new(LatRaw,LonRaw,Lat,Lon,Altitude,GpsValid!=0):null;
            GpsTimeInfo? time=HasGpsTime!=0?new(GpsTime,GpsDate,Speed,Direction):null;
            var list=Text(messages,256).Split(';',StringSplitOptions.RemoveEmptyEntries)
                .Select(m=>{int c=m.IndexOf(':');return new FrameMessage(m[..c],m[(c+1)..]);}).ToArray();
            return new(Seconds,Sample,(FrameKind)Kind,SyncOk!=0,Crc!=0,Voice!=0,[(HalfKind)half[0],(HalfKind)half[1]],
                NewCall!=0,HasCaller!=0?Text(caller,16):null,HasCalled!=0?Text(called,16):null,HasIdentification!=0?Extension:null,
                CommType,Group,CallControl,VoiceMode,LastType,Ended!=0,position,time,Text(name,64),NameFailed!=0,list,
                new ReadOnlySpan<byte>(payload,36).ToArray(),new ReadOnlySpan<byte>(llr,288).ToArray());
        }
    }
}

internal enum FrameKind {Unknown=0,SynchronousBurst=1,Communication=2}
internal enum HalfKind {Tch=0,Facch=1,Pich=2}
internal sealed record Position(uint LatitudeRaw,uint LongitudeRaw,double Latitude,double Longitude,int Altitude,bool Valid) {
    // ddmm.mmmm as transmitted (Annex 2 4.8): "N35°40.2565′"
    public string LatitudeText=>$"N{LatitudeRaw/1000000}°{LatitudeRaw/10000%100:00}.{LatitudeRaw%10000:0000}′";
    public string LongitudeText=>$"E{LongitudeRaw/1000000}°{LongitudeRaw/10000%100:00}.{LongitudeRaw%10000:0000}′";
}
internal sealed record GpsTimeInfo(uint Time,uint Date,int Speed01Knot,int Direction) {
    public bool HasDate=>Date!=0;
    private static readonly string[] Points16=["北","北北東","北東","東北東","東","東南東","南東","南南東","南","南南西","南西","西南西","西","西北西","北西","北北西"];
    // Direction of movement in degrees clockwise from north, as one of 16 compass points
    public string Compass=>Points16[(int)Math.Round(((Direction%360)+360)%360/22.5)%16];
    public string Text=>HasDate?$"20{Date%100:00}-{Date/100%100:00}-{Date/10000:00} {Time/10000:00}:{Time/100%100:00}:{Time%100:00} JST":"未測位";
}
internal sealed record FrameMessage(string Channel,string Type);
internal sealed record Frame(double Seconds,ulong Sample,FrameKind Kind,bool SyncOk,bool Crc,bool Voice,HalfKind[] Halves,
    bool NewCall,string? Caller,string? Called,uint? Extension,int CommType,int Group,int CallControl,int VoiceMode,int LastType,bool Ended,
    Position? Position,GpsTimeInfo? GpsTime,string Name,bool NameFailed,FrameMessage[] Messages,byte[] Payload,byte[] Llr);

internal sealed unsafe class NativeReceiver : IDisposable {
    private const string Library="t99_native";
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr t99_create(double rate);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] private static extern void t99_destroy(IntPtr h);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr t99_error();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] private static extern int t99_process(IntPtr h,float* iq,int count);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] private static extern int t99_read(IntPtr h,out NativeFrame frame);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] private static extern int t99_json(IntPtr h,long burst,byte* text,int capacity);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] private static extern int t99_status(IntPtr h,out ReceiverStats stats);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] private static extern int t99_eye_read(IntPtr h,float* eye,int count);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] private static extern int t99_audio_read(IntPtr h,float* pcm,int count);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] private static extern void t99_audio_flush(IntPtr h);
    private IntPtr handle;
    private readonly byte[] json=new byte[65536];
    private readonly float[] audio=new float[8192];
    private static string Error()=>Marshal.PtrToStringUTF8(t99_error()) ?? "native error";
    public NativeReceiver(double rate) {handle=t99_create(rate);if(handle==IntPtr.Zero) throw new InvalidOperationException(Error());}
    public void Push(ReadOnlySpan<float> iq) {
        fixed(float* p=iq) if(t99_process(handle,p,iq.Length/2)!=0) throw new InvalidOperationException(Error());
    }
    public Frame? Read()=>t99_read(handle,out var frame)>0?frame.Copy():null;
    // JSON line of the frame returned by the last Read(); burst = .burst record index or -1
    public string Json(long burst) {
        int n;fixed(byte* p=json) n=t99_json(handle,burst,p,json.Length);
        return n<0?"{}":Encoding.UTF8.GetString(json,0,n);
    }
    public ReceiverStats Stats() {t99_status(handle,out var stats);return stats;}
    public float[] DrainEye() {
        var eye=new float[256*64];int count;
        fixed(float* data=eye) count=t99_eye_read(handle,data,eye.Length);
        return count>0?eye[..count]:[];
    }
    // Decoded TOKUDER audio (8 kHz) that became available
    public float[] DrainAudio() {
        var result=new List<float>();int n;
        do {fixed(float* p=audio) n=t99_audio_read(handle,p,audio.Length);result.AddRange(audio.AsSpan(0,Math.Max(0,n)).ToArray());} while(n==audio.Length);
        return result.ToArray();
    }
    public void FlushAudio()=>t99_audio_flush(handle);
    public void Dispose() {if(handle!=IntPtr.Zero) t99_destroy(handle);handle=IntPtr.Zero;}
}
