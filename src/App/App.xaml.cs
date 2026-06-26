using System.IO;
using System.Windows;
using ModelCodex.App.Services;
using ModelCodex.App.ViewModels;
using ModelCodex.App.Views;
using Tiger.Model;

namespace ModelCodex.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Self-capture: launch UI, select a model, screenshot the window (incl. the DX viewport) to PNG.
        if (e.Args.Length >= 1 && e.Args[0] == "--shot")
        {
            RunShot(e.Args);
            return;
        }

        // Headless smoke tests (no UI) for CI / verification.
        if (e.Args.Length >= 1 && e.Args[0].StartsWith("--"))
        {
            RunHeadless(e.Args);
            Shutdown(0);
            return;
        }

        var window = new MainWindow { DataContext = new MainWindowViewModel() };
        window.Show();
    }

    // --shot <hashHex> <outPng>  — render the app (incl. preview viewport) to a PNG for self-verification.
    private async void RunShot(string[] args)
    {
        string outPath = args.Length >= 3 ? args[2] : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mc-shot.png");
        try
        {
            uint hash = Convert.ToUInt32(args[1], 16);
            var vm = new MainWindowViewModel();
            var win = new MainWindow { DataContext = vm, Width = 1500, Height = 950, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            win.Show();

            for (int i = 0; i < 120 && !vm.IndexLoaded; i++) await Task.Delay(500);

            var entry = AppState.Instance.Manager?.Models.FirstOrDefault(m => m.TagHash == hash);
            if (entry != null)
            {
                vm.Library.SelectedPackage = vm.Library.Packages.FirstOrDefault(p => p.PkgId == entry.PkgId);
                await Task.Delay(400);
                vm.Library.SelectedTile = vm.Library.VisibleModels.FirstOrDefault(t => t.TagHash == hash);
            }
            await Task.Delay(2500); // let the viewport parse + render

            int w = (int)win.ActualWidth, h = (int)win.ActualHeight;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(win);
            Save(rtb, outPath);
            // Also a crop of the right-hand preview panel (the GPU viewport), scaled up for clarity.
            int cropW = Math.Min(440, w), cropX = Math.Max(0, w - cropW);
            var crop = new System.Windows.Media.Imaging.CroppedBitmap(rtb, new System.Windows.Int32Rect(cropX, 0, cropW, h));
            Save(crop, System.IO.Path.ChangeExtension(outPath, ".preview.png"));
        }
        catch (Exception ex) { File.WriteAllText(outPath + ".err", ex.ToString()); }
        Shutdown(0);
    }

    private static void Save(System.Windows.Media.Imaging.BitmapSource bmp, string path)
    {
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void RunHeadless(string[] args)
    {
        string logDir = args.Length >= 3 ? args[2] : Path.GetTempPath();
        Directory.CreateDirectory(logDir);
        string log = Path.Combine(logDir, "modelcodex-headless.log");
        try
        {
            switch (args[0])
            {
                case "--config":
                    var st = AppState.Instance;
                    File.WriteAllText(log, $"GameDir={st.Config.GameDir}\nValid={st.GameDirValid}\nExportDir={st.EffectiveExportDir}\n");
                    break;

                case "--index-test":
                {
                    var mgr = AppState.Instance.BuildIndex();
                    File.WriteAllText(log, $"models={mgr.Models.Count} textures={mgr.Textures.Count} packages={mgr.PackageCount} pkgGroups={mgr.PackageGroups.Count}\n");
                    break;
                }

                case "--export": // --export <hashHex> <outDir> <format>
                {
                    var mgr = AppState.Instance.BuildIndex();
                    uint hash = Convert.ToUInt32(args[1], 16);
                    var entry = mgr.ModelByTag.TryGetValue(hash, out var me) ? me : throw new Exception("not a model");
                    var geom = ModelParse.Parse(mgr, entry) ?? throw new Exception("parse failed");
                    string path = ModelExporter.Export(mgr, geom, $"{hash:X8}", args[2], args[3], true);
                    File.WriteAllText(log, $"OK {path}\nparts={geom.Parts.Count} verts={geom.VertexCount} tris={geom.TriangleCount}\n");
                    break;
                }

                default:
                    File.WriteAllText(log, $"unknown mode {args[0]}\n");
                    break;
            }
        }
        catch (Exception ex) { File.WriteAllText(log, $"ERROR {ex}\n"); }
    }
}
