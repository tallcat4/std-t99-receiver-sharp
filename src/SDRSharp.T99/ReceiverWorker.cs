using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
namespace SDRSharp.T99;

internal sealed record Snapshot(ReceiverStats Stats,string Status,CallInfo? Call,bool CallActive,bool? Accepted,
    CallInfo[] History,long Drops,ulong AudioDrops) {
    public static readonly Snapshot Idle=new(default,"停止中",null,false,null,[],0,0);
}
internal sealed record EyeBatch(long Epoch,long Timestamp,float[] Traces);

internal sealed class ReceiverWorker : IDisposable {
    private sealed record Block(float[] Data,int Count,double Rate,long Epoch);
    private readonly BlockingCollection<Block> queue=new(512);
    private readonly Thread thread;
    private Settings settings=new();
    private double rate;
    private long epoch,drops,queuedSamples,frequency;
    private int enabled,sourceRunning,disposed;
    public const int HistoryLength=100;
    // The CRC count shown starts again from zero after this long without any frame
    internal static TimeSpan CounterHold=TimeSpan.FromSeconds(10);
    public readonly ConcurrentQueue<EyeBatch> EyeBatches=new();
    public long Epoch=>Interlocked.Read(ref epoch);
    public Snapshot Snapshot=Snapshot.Idle;
    public bool Enabled {get=>Volatile.Read(ref enabled)!=0;set {if(Interlocked.Exchange(ref enabled,value?1:0)!=(value?1:0)) SignalChanged();}}
    public bool SourceRunning {get=>Volatile.Read(ref sourceRunning)!=0;set {if(Interlocked.Exchange(ref sourceRunning,value?1:0)!=(value?1:0)) SignalChanged();}}
    public double SampleRate {set {if(Volatile.Read(ref rate)!=value) {Volatile.Write(ref rate,value);SignalChanged();}}}
    public long Frequency {get=>Interlocked.Read(ref frequency);set=>Interlocked.Exchange(ref frequency,value);}
    public void Configure(Settings next)=>Volatile.Write(ref settings,next);
    public void SignalChanged()=>Interlocked.Increment(ref epoch);
    public ReceiverWorker() {thread=new(Run) {IsBackground=true,Name="T99 selected-channel receiver"};thread.Start();}

    public void Push(ReadOnlySpan<float> iq) {
        if(!Enabled || !SourceRunning || Volatile.Read(ref disposed)!=0 || iq.Length==0) return;
        double r=Volatile.Read(ref rate);long generation=Interlocked.Read(ref epoch);
        if(!double.IsFinite(r) || r<12000 || r>3200000) {
            Volatile.Write(ref Snapshot,Snapshot with {Status="IQレートが範囲外です (12 kHz～3.2 MHz)"});return;
        }
        // Split large host blocks to bound processing and native output queues.
        for(int offset=0;offset<iq.Length;offset+=8192) {
            int count=Math.Min(8192,iq.Length-offset);
            if(Interlocked.Add(ref queuedSamples,count/2)>r*.75) {
                Interlocked.Add(ref queuedSamples,-count/2);Interlocked.Add(ref drops,(iq.Length-offset)/2);SignalChanged();return;
            }
            var data=ArrayPool<float>.Shared.Rent(count);iq.Slice(offset,count).CopyTo(data);
            try {if(queue.TryAdd(new(data,count,r,generation))) continue;} catch(InvalidOperationException) {}
            ArrayPool<float>.Shared.Return(data);Interlocked.Add(ref queuedSamples,-count/2);Interlocked.Add(ref drops,count/2);SignalChanged();return;
        }
    }

    private void Run() {
        NativeReceiver? receiver=null;WaveOutput? audio=null;Recording? recording=null;
        long currentEpoch=-1,calls=0,nextUi=0,nextEye=0;int device=int.MinValue;string recordingFolder="";
        ulong baseFrames=0,baseCrc=0;DateTime lastFrame=DateTime.MinValue;   // CRC count since the last quiet period
        CallInfo? call=null;var history=new List<CallInfo>();var pending=new List<float>();
        void Remember(CallInfo c) {
            if(history.Count>0 && history[0].Number==c.Number) history[0]=c; else history.Insert(0,c);
            if(history.Count>HistoryLength) history.RemoveAt(history.Count-1);
        }
        void Suspend() {
            receiver?.Dispose();receiver=null;audio?.Dispose();audio=null;recording?.Dispose();recording=null;
            call=null;pending.Clear();while(EyeBatches.TryDequeue(out _)){}
        }
        try {
            while(!queue.IsCompleted) {
                if((!Enabled || !SourceRunning) && (receiver!=null || recording!=null)) Suspend();
                Settings options=Volatile.Read(ref settings);
                if((!options.Record || options.Folder!=recordingFolder) && recording!=null) {recording.Dispose();recording=null;}
                if(!queue.TryTake(out var block,100)) continue;
                Interlocked.Add(ref queuedSamples,-block.Count/2);
                try {
                    if(!Enabled || !SourceRunning || block.Epoch!=Interlocked.Read(ref epoch)) continue;
                    if(receiver==null || currentEpoch!=block.Epoch) {Suspend();receiver=new(block.Rate);currentEpoch=block.Epoch;baseFrames=baseCrc=0;}
                    if(!options.Playback || (audio!=null && device!=options.Device)) {audio?.Dispose();audio=null;}
                    if(options.Playback && audio==null) {audio=new(options.Device);device=options.Device;}
                    if(options.Record && recording==null) {recording=new(options.Folder);recordingFolder=options.Folder;}
                    receiver.Push(block.Data.AsSpan(0,block.Count));
                    var now=DateTimeOffset.Now;
                    Frame? frame;
                    while((frame=receiver.Read())!=null) {
                        recording?.Frame(frame,receiver,now);
                        lastFrame=DateTime.Now;
                        // A synchronous burst with its identification starts a transmission; frames heard without one
                        // (joined mid-call) still form a call.
                        if(call==null || frame.NewCall || (call.Ended && frame.Kind==FrameKind.SynchronousBurst)) {
                            if(call!=null) Remember(call);
                            call=new CallInfo {Number=++calls,Started=now};
                        }
                        call=call.Apply(frame,now);
                        Remember(call);
                    }
                    // Audio: every decoded sample is recorded; playback follows the standby setting (Table Annex 2-4-14)
                    var pcm=receiver.DrainAudio();
                    if(pcm.Length>0) {
                        recording?.Audio(pcm);
                        bool play=call!=null && Selective.Accepts(call,options.Standby,options.OwnId,options.Group)==true;
                        if(audio!=null && play) {
                            pending.AddRange(pcm);
                            int whole=pending.Count/160*160;
                            for(int i=0;i<whole;i+=160) {
                                float[] part=new float[160];
                                for(int j=0;j<160;j++) part[j]=pending[i+j]*options.Volume/100f;
                                audio.Write(part);
                            }
                            pending.RemoveRange(0,whole);
                        } else pending.Clear();
                    }
                    var stats=receiver.Stats();
                    // Counts since the frames began; after CounterHold without a frame they start again from zero
                    if(stats.Frames>baseFrames && DateTime.Now-lastFrame>CounterHold) {baseFrames=stats.Frames;baseCrc=stats.CrcOk;}
                    stats.Frames-=baseFrames;stats.CrcOk-=Math.Min(baseCrc,stats.CrcOk);
                    long tick=Stopwatch.GetTimestamp();
                    if(tick>=nextEye) {
                        var eye=receiver.DrainEye();
                        if(eye.Length>0) {EyeBatches.Enqueue(new(currentEpoch,tick,eye));while(EyeBatches.Count>8) EyeBatches.TryDequeue(out _);}
                        nextEye=tick+Stopwatch.Frequency/60;
                    }
                    if(tick>=nextUi) {
                        bool active=call!=null && !call.Ended && (now-call.LastFrame).TotalSeconds<1.0;
                        bool? accepted=call==null?null:Selective.Accepts(call,options.Standby,options.OwnId,options.Group);
                        string status=stats.Locked!=0?"受信中・同期":active?"受信中":"探索中";
                        Volatile.Write(ref Snapshot,new(stats,status,call,active,accepted,history.ToArray(),Interlocked.Read(ref drops),audio?.DroppedBlocks??0));
                        nextUi=tick+Stopwatch.Frequency/5;
                    }
                } catch(Exception e) {Volatile.Write(ref Snapshot,Snapshot with {Status=e.Message});Enabled=false;Suspend();}
                finally {ArrayPool<float>.Shared.Return(block.Data);}
            }
        } catch(Exception e) {Volatile.Write(ref Snapshot,Snapshot with {Status=e.Message});Enabled=false;}
        finally {Suspend();}
    }

    public void Dispose() {
        if(Interlocked.Exchange(ref disposed,1)!=0) return;
        Enabled=false;queue.CompleteAdding();thread.Join();queue.Dispose();
    }
}

// .burst / .llr (TCH/TCH frames), .jsonl (every frame), .wav (decoded TOKUDER audio). FORMAT.md
internal sealed class Recording : IDisposable {
    private readonly BinaryWriter bursts,llr;
    private readonly StreamWriter events;
    private readonly WaveWriter wave;
    private long records;
    public string Stem {get;}
    public Recording(string folder) {
        if(string.IsNullOrWhiteSpace(folder)) throw new IOException("録音先フォルダーを選んでください");
        Directory.CreateDirectory(folder);
        Stem=Path.Combine(folder,$"T99-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        // Every stream uses CreateNew; no existing capture can be overwritten.
        bursts=new(new FileStream(Stem+".burst",FileMode.CreateNew));
        try {
            llr=new(new FileStream(Stem+".llr",FileMode.CreateNew));
            events=new(new FileStream(Stem+".jsonl",FileMode.CreateNew));wave=new(Stem+".wav");
        } catch {bursts.Dispose();llr?.Dispose();events?.Dispose();throw;}
    }
    public void Frame(Frame f,NativeReceiver receiver,DateTimeOffset now) {
        long burst=-1;
        if(f.Voice) {
            bursts.Write((byte)0xff);for(int i=0;i<4;i++) {bursts.Write((byte)0x48);bursts.Write(f.Payload,i*9,9);}
            llr.Write(f.Llr);burst=records++;
        }
        string json=receiver.Json(burst);
        events.WriteLine("{\"utc\":"+JsonSerializer.Serialize(now.ToUniversalTime())+","+json[1..]);
    }
    public void Audio(float[] pcm)=>wave.Write(pcm);
    public void Dispose() {try {wave.Dispose();} finally {bursts.Dispose();llr.Dispose();events.Dispose();}}
}
