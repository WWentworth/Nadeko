#nullable disable
using CommandLine;

namespace NadekoBot.Modules.Gambling.Common.AnimalRacing;

public class RaceOptions : INadekoCommandOptions
{
    [Option('s', "start-time", Default = 20, Required = false)]
    public int StartTime { get; set; } = 20;

    public void NormalizeOptions()
    {
        if (this.StartTime is < 10 or > 120)
            this.StartTime = 20;
    }
}
