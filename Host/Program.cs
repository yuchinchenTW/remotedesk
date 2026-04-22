using System;
using System.Windows.Forms;
using SimpleRemote.Shared;

namespace SimpleRemote.Host
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            DpiAwareness.Enable();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new HostForm());
        }
    }
}
