using System.ComponentModel;
using System.Diagnostics;
using SDRSharp.Common;
using SDRSharp.Radio;
namespace SDRSharp.T99;

public sealed unsafe class T99Plugin : ISharpPlugin,IIQProcessor {
    private ISharpControl? host;
    private ReceiverPanel? panel;
    private ReceiverWorker? worker;
    private System.Windows.Forms.Timer? timer;
    private bool changedMute,oldMute;
    private DetectorType oldDetector;
    private int oldBandwidth;
    private Settings settings=new();
    public string DisplayName=>"STD-T99 Receiver";
    public UserControl Gui=>panel??throw new InvalidOperationException("Not initialized");
    public bool Enabled {get=>worker?.Enabled??false;set {if(worker!=null) worker.Enabled=value;}}
    public double SampleRate {set {if(worker!=null) worker.SampleRate=value;}}
    public void Initialize(ISharpControl control) {
        if(host!=null) throw new InvalidOperationException("Already initialized");
        host=control;panel=new();worker=new();
        panel.Apply(Settings.Load());panel.SetFrequency(control.Frequency,control.CenterFrequency);
        settings=panel.Read();worker.Configure(settings);worker.SourceRunning=control.IsPlaying;worker.Frequency=control.Frequency;
        panel.SettingsChanged+=()=>{
            settings=panel.Read();worker.Configure(settings);
            try {settings.Save();} catch(Exception e) {panel.ShowError(e.Message);}
        };
        panel.ActiveChanged+=SetActive;
        panel.TuneRequested+=Tune;
        control.RegisterStreamHook(this,ProcessorType.DecimatedAndFilteredIQ);
        control.PropertyChanged+=HostChanged;
        // A 25 ms request avoids the common ~47 ms quantization of a 33 ms WinForms timer, keeping the eye near 30 fps.
        timer=new() {Interval=25};
        long nextText=0;
        timer.Tick+=(_,_)=>{
            var state=Volatile.Read(ref worker.Snapshot);
            var batches=new List<EyeBatch>();while(worker.EyeBatches.TryDequeue(out var batch))batches.Add(batch);
            panel.RefreshEye(batches,worker.Epoch,worker.Enabled&&worker.SourceRunning&&state.Stats.Active!=0);
            long now=Stopwatch.GetTimestamp();
            if(now>=nextText) {
                if(!worker.Enabled && state.Status is "受信中・同期" or "受信中" or "探索中") state=state with {Status="停止中"};
                panel.RefreshState(state,worker.Enabled,worker.SourceRunning,settings);nextText=now+Stopwatch.Frequency/5;
            }
            if(changedMute&&!worker.Enabled) RestoreMute();
        };
        timer.Start();
    }
    // Channel chosen in the panel. With the LO offset on, the tuner's centre (LO: DC spike, IQ image) is put
    // below the channel by the offset, so no channel is ever received at the LO.
    private void Tune(long frequency) {
        if(host==null || worker==null) return;
        long? center=LoCenter(frequency,settings,host.SourceIsTunable&&!host.CenterFrequencyIsLocked,host.InputSampleRate);
        if(center.HasValue) host.CenterFrequency=center.Value;
        host.Frequency=frequency;
        worker.Frequency=host.Frequency;worker.SignalChanged();   // also when the host raises no change event
        panel?.SetFrequency(host.Frequency,host.CenterFrequency);
    }
    // The LO for a channel, or null to leave it alone (offset off, fixed centre, not a tuner, too narrow a band).
    // The offset is limited to 40 % of the IQ rate so that the channel stays well inside the band.
    internal static long? LoCenter(long frequency,Settings settings,bool tunable,double inputRate) {
        if(!settings.LoOffset || !tunable) return null;
        long offset=settings.LoOffsetKHz*1000L;
        if(double.IsFinite(inputRate) && inputRate>0) offset=Math.Min(offset,(long)(inputRate*0.4));
        return offset>=10000?frequency-offset:null;
    }
    public void Process(Complex* buffer,int length) {
        if(buffer!=null && length>0 && length<=8388608) worker?.Push(new ReadOnlySpan<float>((float*)buffer,length*2));
    }
    private void SetActive(bool active) {
        if(host==null || worker==null) return;
        if(active) {
            // SDR#'s own audio is muted: the plugin plays the decoded TOKUDER voice itself.
            oldMute=host.AudioIsMuted;host.AudioIsMuted=true;changedMute=true;
            oldDetector=host.DetectorType;oldBandwidth=host.FilterBandwidth;
            host.DetectorType=DetectorType.NFM;host.FilterBandwidth=8000;
            worker.SourceRunning=host.IsPlaying;worker.Enabled=true;
            try {if(!host.IsPlaying) host.StartRadio();}
            catch {worker.Enabled=false;RestoreMute();throw;}
        } else {worker.Enabled=false;RestoreMute();}
    }
    private void RestoreMute() {
        if(changedMute && host!=null) {
            if(host.AudioIsMuted) host.AudioIsMuted=oldMute;
            if(host.DetectorType==DetectorType.NFM && host.FilterBandwidth==8000) {host.DetectorType=oldDetector;host.FilterBandwidth=oldBandwidth;}
            changedMute=false;
        }
    }
    private void HostChanged(object? sender,PropertyChangedEventArgs e) {
        if(worker==null || host==null) return;
        if(e.PropertyName is "StartRadio" or "StopRadio" or "IsPlaying") worker.SourceRunning=host.IsPlaying;
        if(e.PropertyName is "Frequency" or "CenterFrequency" or "InputSampleRate" or "Source") {
            worker.Frequency=host.Frequency;worker.SignalChanged();
            if(panel?.IsHandleCreated==true) try {panel.BeginInvoke((Action)(()=>{if(host!=null) panel?.SetFrequency(host.Frequency,host.CenterFrequency);}));} catch(InvalidOperationException) {}
        }
    }
    public void Close() {
        timer?.Stop();timer?.Dispose();timer=null;
        if(host!=null) {host.PropertyChanged-=HostChanged;host.UnregisterStreamHook(this);}
        worker?.Dispose();worker=null;RestoreMute();panel?.Dispose();panel=null;host=null;
    }
}
