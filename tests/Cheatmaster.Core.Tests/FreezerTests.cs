using Cheatmaster.Core.Cheats;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;
using Xunit;

namespace Cheatmaster.Core.Tests;

public class FreezerTests
{
    /// <summary>A frozen value keeps being put back, for as long as it stays frozen — not just the first few times.</summary>
    [Fact]
    public void A_frozen_value_is_put_back_every_time_the_game_changes_it()
    {
        using var target = WatchTargetProcess.Start("hold", "5000");
        using var process = TargetProcess.Open(target.Pid, out string why);
        Assert.True(process is not null, why);

        var entry = new CheatEntry { Address = AddressSpec.ForAddress(process!, target.Field), FreezeValue = "4242", Frozen = true };
        entry.SetInterpretation(Interpretation.Plain(ScanType.Int32));

        using var freezer = new ValueFreezer();
        freezer.Attach(process);
        freezer.Update([entry]);

        for (int round = 0; round < 10; round++)
        {
            Assert.True(process!.WriteValue(target.Field, round));
            var until = DateTime.UtcNow.AddSeconds(2);
            while (process.ReadValue<int>(target.Field) != 4242 && DateTime.UtcNow < until) Thread.Sleep(10);
            Assert.True(process.ReadValue<int>(target.Field) == 4242, $"round {round}: not put back after {freezer.Writes} writes");
        }
    }
}
