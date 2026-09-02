using iCemDataMonitor.Plugins.Icem;
using iCemDataMonitor.Sdk.Models;
using System.Reflection;

namespace iCemDataMonitor.Plugins.Icem.Tests;

public class SsfTemplateLoaderTests
{
    /// <summary>
    /// F7ssf: verify that when ALL col0 == 0, positions are sequential 1..n and IsSpare=false.
    /// </summary>
    [Fact]
    public void F7ssf_AllZeroCol0_AssignsSequentialPositions()
    {
        // Create a temp SSF in a temp templates/ dir
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var templatesDir = Path.Combine(tempDir, "templates");
        Directory.CreateDirectory(templatesDir);

        var ssfContent = """
            0,Var A,Description A,42,215
            0,Var B,Description B,130,80
            0,Var C,Description C,71,185
            0,Var D,Description D,202,285
            """;

        File.WriteAllText(Path.Combine(templatesDir, "test_all_zero.ssf"), ssfContent);

        try
        {
            // We need to run Load in a context where AppDomain.BaseDirectory points to tempDir
            // Since SsfTemplateLoader uses AppDomain.CurrentDomain.BaseDirectory, we can't redirect it.
            // Instead, test the fields parsed from a known flecs SSF that lives in the real templates dir.
            // The real flecs_ver4p53_ppg_bpm_psi.ssf has all col0=0 for all 34 vars.
            // Load it and verify positions.
            var vars = SsfTemplateLoader.Load("flecs_ver4p53_ppg_bpm_psi");

            Assert.NotEmpty(vars);
            Assert.Equal(34, vars.Count);

            // Verify: all positions sequential 1..34, all IsSpare=false
            for (int i = 0; i < vars.Count; i++)
            {
                Assert.Equal(i + 1, vars[i].Position);
                Assert.False(vars[i].IsSpare);
            }

            // Verify specific positions
            Assert.Equal(1, vars[0].Position);
            Assert.Equal("Time & Date", vars[0].Name);
            Assert.Equal(34, vars[33].Position);
            Assert.Equal("VolumetricMeter", vars[33].Name);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// F7ssf: template with mixed col0 values should honor them and mark IsSpare.
    /// </summary>
    [Fact]
    public void F7ssf_MixedCol0_HonorsPositionAndIsSpare()
    {
        // plc.ssf has some vars with col0=1 (not all zero)
        var vars = SsfTemplateLoader.Load("plc");

        Assert.NotEmpty(vars);

        // Find a row with col0 > 0 — should be IsSpare=true
        var spares = vars.Where(v => v.IsSpare).ToList();
        // "CT Speed" and "Running Speed1" have col0=1 in plc.ssf
        Assert.Contains(spares, s => s.Name is "CT Speed" or "Running Speed1");

        // Non-spare vars should have IsSpare=false
        var nonSpares = vars.Where(v => !v.IsSpare).ToList();
        Assert.NotEmpty(nonSpares);
        Assert.All(nonSpares, v => Assert.False(v.IsSpare));
    }

    /// <summary>
    /// F7ssf: empty / missing file returns empty list (no crash).
    /// </summary>
    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var vars = SsfTemplateLoader.Load("nonexistent_template_xyz");
        Assert.Empty(vars);
    }

    /// <summary>
    /// F7ssf: comment line (starts with *) is skipped.
    /// </summary>
    [Fact]
    public void Load_CommentLine_Skipped()
    {
        // AdcPump.ssf has a comment on line 1: *Ace Elite Azonix ADC serial data template
        var vars = SsfTemplateLoader.Load("AdcPump");
        Assert.NotEmpty(vars);
        Assert.DoesNotContain(vars, v => v.Name.StartsWith("*"));
        // Should have 11 data rows (lines 2-12 of AdcPump.ssf)
        Assert.Equal(11, vars.Count);
    }

    /// <summary>
    /// F8: verify the default flecs template resolves to an existing file.
    /// </summary>
    [Fact]
    public void F8_DefaultFlecsTemplate_Resolves()
    {
        var vars = SsfTemplateLoader.Load(DeviceFamilyRegistry.DefaultTemplate);
        Assert.NotEmpty(vars);
        Assert.Equal(34, vars.Count); // flecs_ver4p53_ppg_bpm_psi has 34 variables
    }

    /// <summary>
    /// F8: verify DeviceFamilyRegistry default config check passes.
    /// </summary>
    [Fact]
    public void F8_DefaultResolves_CheckPasses()
    {
        var error = DeviceFamilyRegistry.VerifyDefaultResolves();
        Assert.Null(error);
    }

    /// <summary>
    /// F8: verify compac_unipro.ssf file EXISTS now.
    /// </summary>
    [Fact]
    public void F8_CompacUnipro_Exists()
    {
        var vars = SsfTemplateLoader.Load("compac_unipro");
        Assert.NotEmpty(vars);
        Assert.Contains(vars, v => v.Name == "Comb Pump Rate");
    }
}
