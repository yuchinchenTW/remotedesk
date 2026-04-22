using System;
using System.Windows.Forms;
using SimpleRemote.Shared;

namespace SimpleRemote.Viewer
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            DpiAwareness.Enable();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ViewerForm());
        }
    }
}
