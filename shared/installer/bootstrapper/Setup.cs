using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("iCem Data Monitor Setup")]
[assembly: AssemblyDescription("Install and remove iCem Data Monitor MSI products")]
[assembly: AssemblyCompany("Burservis")]
[assembly: AssemblyProduct("iCem Data Monitor")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace IcemSetup
{
    public sealed class Product
    {
        public string Code { get; }
        public string Family { get; }
        public string Name { get; }
        public Product(string code, string family, string name)
        {
            Code = Guid.Parse(code).ToString("B").ToUpperInvariant();
            Family = family; Name = name;
        }
        public override string ToString() => Name + " [" + Family + "]";
    }

    public static class Policy
    {
        // Re-enumerate before each mutation; never accept an arbitrary ProductCode.
        public static string RemoveArguments(string selected, IEnumerable<Product> installed)
        {
            var code = Guid.Parse(selected).ToString("B").ToUpperInvariant();
            if (!installed.Any(p => p.Code == code)) throw new InvalidOperationException("Выбранный продукт больше не установлен.");
            return "/x " + code;
        }
        public static bool CanInstall(string payloadFamily, IEnumerable<Product> installed) =>
            installed.All(p => p.Family == payloadFamily);
    }

    public static class Inventory
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        static extern uint MsiEnumRelatedProducts(string upgrade, uint reserved, uint index, StringBuilder product);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        static extern uint MsiGetProductInfo(string product, string property, StringBuilder value, ref uint size);
        static string Info(string code, string property)
        {
            uint size = 0;
            uint result = MsiGetProductInfo(code, property, null, ref size);
            if (result != 0 && result != 234) throw new InvalidOperationException("MSI product query: " + result);
            var value = new StringBuilder(checked((int)size + 1));
            size++;
            result = MsiGetProductInfo(code, property, value, ref size);
            if (result != 0) throw new InvalidOperationException("MSI product query: " + result);
            return value.ToString();
        }
        public static List<Product> Read()
        {
            var products = new List<Product>();
            foreach (var line in Program.Resource("families.txt").Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('|');
                if (fields.Length != 2) throw new InvalidDataException("Invalid family inventory");
                for (uint index = 0; ; index++)
                {
                    var code = new StringBuilder(39);
                    uint result = MsiEnumRelatedProducts(Guid.Parse(fields[1]).ToString("B"), 0, index, code);
                    if (result == 259) break;
                    if (result != 0) throw new InvalidOperationException("MSI enumeration: " + result);
                    products.Add(new Product(code.ToString(), fields[0], Info(code.ToString(), "InstalledProductName") + " " + Info(code.ToString(), "VersionString")));
                }
            }
            return products;
        }
    }

    public static class Program
    {
        public static string Resource(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidDataException("Missing resource: " + name);
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }
        public static byte[] Payload()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.msi"))
            using (var buffer = new MemoryStream())
            {
                if (stream == null) throw new InvalidDataException("Missing MSI payload");
                stream.CopyTo(buffer);
                var data = buffer.ToArray();
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant() != Resource("payload.sha256").Trim())
                        throw new InvalidDataException("MSI payload checksum mismatch");
                return data;
            }
        }
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--verify") { Payload(); Console.WriteLine("PASS embedded MSI SHA256"); return 0; }
                if (args.Length == 1 && args[0] == "--list")
                {
                    foreach (var product in Inventory.Read()) Console.WriteLine(product.Code + "\t" + product);
                    return 0;
                }
                if (args.Length != 0) throw new ArgumentException("Supported: --list, --verify, or no arguments for setup UI");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); if (args.Length == 0) MessageBox.Show(e.Message, "iCem Setup", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }

    sealed class SetupForm : Form
    {
        readonly ListBox products = new ListBox { Dock = DockStyle.Fill };
        readonly Button remove = new Button { Text = "Удалить выбранную", AutoSize = true };
        readonly Button install = new Button { AutoSize = true };
        readonly Button refresh = new Button { Text = "Обновить список", AutoSize = true };
        readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 65, Padding = new Padding(8) };
        bool busy;
        readonly string payloadFamily = Program.Resource("payload.family").Trim();
        public SetupForm()
        {
            Text = "iCem Data Monitor — установка и удаление";
            ClientSize = new Size(750, 330); MinimumSize = new Size(750, 330);
            StartPosition = FormStartPosition.CenterScreen;
            install.Text = "Установить / восстановить " + payloadFamily;
            var title = new Label { Text = "Установленные версии (.NET и Rust). Выберите версию для удаления:", Dock = DockStyle.Top, Height = 45, Padding = new Padding(8) };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45, Padding = new Padding(5) };
            actions.Controls.AddRange(new Control[] { remove, install, refresh });
            Controls.Add(products); Controls.Add(title); Controls.Add(actions); Controls.Add(status);
            products.SelectedIndexChanged += (s, e) => remove.Enabled = !busy && products.SelectedItem != null;
            refresh.Click += (s, e) => Reload();
            remove.Click += async (s, e) => await Operate(true);
            install.Click += async (s, e) => await Operate(false);
            FormClosing += (s, e) => { if (busy) e.Cancel = true; };
            Reload();
        }
        void Reload()
        {
            products.Items.Clear();
            var found = Inventory.Read();
            foreach (var item in found) products.Items.Add(item);
            if (products.Items.Count > 0) products.SelectedIndex = 0;
            remove.Enabled = products.SelectedItem != null;
            install.Enabled = Policy.CanInstall(payloadFamily, found);
            status.Text = install.Enabled ? "Удаление выполняется штатным Windows Installer с подтверждением и запросом UAC. Пользовательские данные не очищаются этим launcher." : "Сначала выберите другое семейство в списке и нажмите «Удалить выбранную». Автоматическое удаление при установке не выполняется.";
        }
        async Task Operate(bool uninstall)
        {
            string work = null;
            try
            {
                string arguments;
                if (uninstall)
                {
                    var selected = products.SelectedItem as Product;
                    if (selected == null) return;
                    if (MessageBox.Show("Удалить " + selected + "?\nУдаление выполнит его собственный MSI.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    arguments = Policy.RemoveArguments(selected.Code, Inventory.Read());
                }
                else
                {
                    if (!Policy.CanInstall(payloadFamily, Inventory.Read())) throw new InvalidOperationException("Сначала удалите другое семейство из списка.");
                    var data = Program.Payload();
                    work = Path.Combine(Path.GetTempPath(), "icem-setup-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(work);
                    var path = Path.Combine(work, "monitor.msi");
                    File.WriteAllBytes(path, data);
                    arguments = "/i \"" + path + "\"";
                }
                string log = Path.Combine(Path.GetTempPath(), "icem-msi-" + Guid.NewGuid().ToString("N") + ".log");
                busy = true; remove.Enabled = install.Enabled = refresh.Enabled = false;
                status.Text = "Дождитесь завершения Windows Installer. Журнал: " + log;
                int code = await Task.Run(() =>
                {
                    using (var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), arguments + " /norestart /L*v \"" + log + "\"") { UseShellExecute = true }))
                    {
                        if (process == null) throw new IOException("Windows Installer не запущен");
                        process.WaitForExit(); return process.ExitCode;
                    }
                });
                if (code != 0 && code != 1602 && code != 3010 && code != 1641) throw new InvalidOperationException("Windows Installer: " + code + "\nЖурнал: " + log);
                if (code == 3010 || code == 1641) MessageBox.Show("Для завершения операции требуется перезагрузка Windows.", Text);
            }
            catch (Exception e) { MessageBox.Show(e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                if (work != null)
                {
                    try { Directory.Delete(work, true); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        MessageBox.Show("Не удалось удалить временный MSI: " + work + "\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                busy = false; refresh.Enabled = true;
                try { Reload(); } catch (Exception e) { remove.Enabled = install.Enabled = false; status.Text = e.Message; }
            }
        }
    }
}
