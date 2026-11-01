using System.Text.Json;
namespace SDRSharp.T99;

internal sealed record Settings(int Device=-1,bool Playback=true,int Volume=80,bool Record=false,string Folder="",
    StandbyMode Standby=StandbyMode.Monitor,string OwnId="",int Group=0,bool Map=true,bool LoOffset=true,int LoOffsetKHz=100) {
    private static string FilePath=>Environment.GetEnvironmentVariable("T99_SETTINGS_PATH") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"T99Receiver","settings.json");
    public static Settings Load() {
        try {
            var s=JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            return s with {Volume=Math.Clamp(s.Volume,0,100),Record=false,Group=Math.Clamp(s.Group,0,63),LoOffsetKHz=Math.Clamp(s.LoOffsetKHz,10,1000),
                Standby=Enum.IsDefined(s.Standby)?s.Standby:StandbyMode.Monitor,OwnId=Selective.Normalize(s.OwnId??"")??""};
        } catch {return new();}
    }
    public void Save() {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath+".tmp",JsonSerializer.Serialize(this with {Record=false}));
        File.Move(FilePath+".tmp",FilePath,true);
    }
}
