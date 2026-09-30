using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AlbumArtTool;
using AlbumArtTool.Core;

internal static partial class Tests
{
    private static void CountedScanProgress()
    {
        string root = NewFolder("scan-progress"), album = Path.Combine(root, "A"), broken = Path.Combine(root, "B");
        Directory.CreateDirectory(album); Directory.CreateDirectory(broken);
        CopyTrack(album, "one.mp3"); CopyTrack(album, "two.mp3");
        File.WriteAllText(Path.Combine(broken, "broken.mp3"), "not an audio stream");
        CopyTrack(root, ".aat-temporary.mp3"); File.WriteAllText(Path.Combine(root, "notes.txt"), "not music");
        File.WriteAllBytes(Path.Combine(root, "cover.jpg"), Cover);
        string backup = Path.Combine(root, ".album-art-backups"); Directory.CreateDirectory(backup); CopyTrack(backup, "saved.mp3");
        var updates = new List<ProgressInfo>();
        var result = Scanner.Scan(root, CancellationToken.None, new DirectProgress<ProgressInfo>(updates.Add));
        Assert(updates[0].Total == 0 && updates[0].Message.StartsWith("Counting"), "scan starts with a separate counting phase");
        var reading = updates.Where(p => p.Total > 0).ToArray();
        Assert(reading.All(p => p.Total == 3), "scan counts supported tracks and excludes backups, temporary copies and images");
        Assert(reading.First().Completed == 0 && reading.Any(p => p.Completed > 0 && p.Completed < p.Total), "scan reports a determinate start and intermediate progress");
        Assert(reading.Last().Completed == 3 && result.FilesRead == 2 && result.Issues.Count == 1,
            "unreadable tracks count as checked so progress reaches 100 percent");
        Assert(reading.Select(p => p.Completed).SequenceEqual(reading.Select(p => p.Completed).OrderBy(n => n)), "scan counter never moves backwards");
        Assert(reading.Last().Message.StartsWith("3/3 tracks scanned"), "scan status includes the numeric checked/total tracker");
        using (var cancel = new CancellationTokenSource())
        {
            result = Scanner.Scan(root, cancel.Token, new DirectProgress<ProgressInfo>(p => { if (p.Total > 0 && p.Completed == 1) cancel.Cancel(); }));
            Assert(result.Cancelled && result.FilesRead == 0 && result.Albums.Count == 0, "cancelling the counted scan excludes a partly scanned album");
        }
        using (var cancel = new CancellationTokenSource())
        {
            result = Scanner.Scan(root, cancel.Token, new DirectProgress<ProgressInfo>(p => cancel.Cancel()));
            Assert(result.Cancelled && result.FilesRead == 0, "counting can be cancelled before tags are opened");
        }
    }

    private static async Task ScanningGuiTest(MainForm form, string output)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var setBusy = typeof(MainForm).GetMethod("SetBusy", flags);
        setBusy.Invoke(form, new object[] { true, false });
        var reporter = (IProgress<ProgressInfo>)typeof(MainForm).GetMethod("Reporter", flags).Invoke(form, null);
        var bar = Descendants(form).OfType<ProgressBar>().Single();
        Assert(bar.Style == ProgressBarStyle.Marquee, "discovery uses an indeterminate progress bar");
        reporter.Report(new ProgressInfo { Completed = 2751, Total = 3867, Message = "2751/3867 tracks scanned · Music" });
        await Task.Delay(100);
        Assert(bar.Visible && bar.Style == ProgressBarStyle.Continuous && bar.Value == 71, "Windows scan bar displays checked files as a percentage");
        Assert(Descendants(form).OfType<Label>().Single(l => l.AccessibleName == "Progress percentage").Text == "71%", "percentage is visible beside the scan bar");
        Assert(Descendants(form).OfType<Label>().Single(l => l.AccessibleName == "Operation status and track counter").Text.StartsWith("2751/3867"), "numeric tracker is shown during scanning");
        Assert(Descendants(form).OfType<Label>().Any(l => l.Visible && l.Text.StartsWith("Scanning your music")), "scanning does not show a false no-files message");
        Assert(Descendants(form).OfType<CheckBox>().All(c => !c.Enabled), "options stay noninteractive during scanning");
        Directory.CreateDirectory(output);
        using (var bitmap = new Bitmap(form.Width, form.Height))
        { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "scanning-progress.png"), ImageFormat.Png); }
        setBusy.Invoke(form, new object[] { false, false });
    }
}
