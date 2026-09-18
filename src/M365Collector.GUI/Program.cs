using M365Collector.Contracts;
using M365Collector.Core;
using M365Collector.Security;

namespace M365Collector.GUI;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            WindowsSecurity.RequireAdministrator();
            var root = InstallationRegistry.DataRoot;
            var state = SetupDetection.Detect(root);
            if (state == SetupState.Broken)
                throw new InvalidDataException("The registered runtime is missing, damaged or uses an unsupported schema. Restore its configuration/database at the registered DataRoot. Setup will not silently replace customer data.");
            RuntimeConfig config;
            var firstRun = state != SetupState.Complete;
            if (firstRun)
            {
                using var wizard = new FirstRunWizard(root);
                if (wizard.ShowDialog() != DialogResult.OK || wizard.CompletedConfig == null) return;
                config = wizard.CompletedConfig;
            }
            else config = new ConfigurationStore(new RuntimePaths(root!)).Load();
            if (config.InstalledVersion != ProductInfo.Version)
                throw new InvalidOperationException("This executable does not match the installed version. Launch M365Collector from " + config.InstallRoot);
            new StructuredLog(new RuntimePaths(config.DataRoot), "gui").Write("ApplicationStartup", "Success");
            Application.Run(new MainForm(config, firstRun));
        }
        catch (Exception exception)
        {
            // Startup/configuration failures contain no Microsoft token responses.
            MessageBox.Show(exception.Message, "M365Collector " + ProductInfo.Version + " — unable to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
