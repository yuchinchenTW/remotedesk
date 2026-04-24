using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;

namespace SimpleRemote.Host
{
    internal sealed class HostForm : Form
    {
        private readonly TextBox _portTextBox;
        private readonly TextBox _passwordTextBox;
        private readonly TextBox _displayNameTextBox;
        private readonly Button _startButton;
        private readonly Button _stopButton;
        private readonly Label _statusLabel;
        private readonly Label _clientLabel;
        private readonly TextBox _ipTextBox;
        private readonly Label _displayLabel;

        private RemoteHostServer _server;

        public HostForm()
        {
            Text = "Simple Remote Host";
            Width = 520;
            Height = 400;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var instructions = new Label
            {
                Left = 20,
                Top = 18,
                Width = 460,
                Height = 36,
                Text = "LAN-only MVP. Viewer instances on the same LAN auto-discover this host over UDP. Run as administrator if you need to control elevated windows."
            };

            var portLabel = new Label
            {
                Left = 20,
                Top = 72,
                Width = 80,
                Text = "Port"
            };

            _portTextBox = new TextBox
            {
                Left = 100,
                Top = 68,
                Width = 120,
                Text = "5901"
            };

            var passwordLabel = new Label
            {
                Left = 20,
                Top = 108,
                Width = 80,
                Text = "Password"
            };

            _passwordTextBox = new TextBox
            {
                Left = 100,
                Top = 104,
                Width = 220,
                UseSystemPasswordChar = true,
                Text = "changeme"
            };

            var displayNameLabel = new Label
            {
                Left = 20,
                Top = 144,
                Width = 80,
                Text = "Name"
            };

            _displayNameTextBox = new TextBox
            {
                Left = 100,
                Top = 140,
                Width = 220,
                Text = Environment.MachineName
            };

            _startButton = new Button
            {
                Left = 350,
                Top = 68,
                Width = 110,
                Text = "Start Host"
            };
            _startButton.Click += StartButton_Click;

            _stopButton = new Button
            {
                Left = 350,
                Top = 102,
                Width = 110,
                Text = "Stop",
                Enabled = false
            };
            _stopButton.Click += StopButton_Click;

            _statusLabel = new Label
            {
                Left = 20,
                Top = 188,
                Width = 460,
                Height = 22,
                Text = "Status: Idle."
            };

            _clientLabel = new Label
            {
                Left = 20,
                Top = 214,
                Width = 460,
                Height = 22,
                Text = "Viewer: No viewer connected."
            };

            _displayLabel = new Label
            {
                Left = 20,
                Top = 240,
                Width = 460,
                Height = 22,
                Text = "Display: " + GetDisplaySummary()
            };

            _ipTextBox = new TextBox
            {
                Left = 20,
                Top = 266,
                Width = 460,
                Height = 90,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Text = BuildLocalIpText()
            };

            Controls.Add(instructions);
            Controls.Add(portLabel);
            Controls.Add(_portTextBox);
            Controls.Add(passwordLabel);
            Controls.Add(_passwordTextBox);
            Controls.Add(displayNameLabel);
            Controls.Add(_displayNameTextBox);
            Controls.Add(_startButton);
            Controls.Add(_stopButton);
            Controls.Add(_statusLabel);
            Controls.Add(_clientLabel);
            Controls.Add(_displayLabel);
            Controls.Add(_ipTextBox);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_server != null)
            {
                _server.Dispose();
            }

            base.OnFormClosed(e);
        }

        private void StartButton_Click(object sender, EventArgs e)
        {
            int port;
            if (!int.TryParse(_portTextBox.Text, out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, "Enter a valid TCP port.", "Invalid Port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_passwordTextBox.Text))
            {
                MessageBox.Show(this, "Set a non-empty password.", "Invalid Password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _server = new RemoteHostServer(UpdateStatus, UpdateClient);
                _server.Start(port, _passwordTextBox.Text, _displayNameTextBox.Text);
                _startButton.Enabled = false;
                _stopButton.Enabled = true;
                _portTextBox.Enabled = false;
                _passwordTextBox.Enabled = false;
                _displayNameTextBox.Enabled = false;
                _displayLabel.Text = "Display: " + GetDisplaySummary();
                _ipTextBox.Text = BuildLocalIpText();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed to Start Host", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopButton_Click(object sender, EventArgs e)
        {
            StopServer();
        }

        private void StopServer()
        {
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }

            _startButton.Enabled = true;
            _stopButton.Enabled = false;
            _portTextBox.Enabled = true;
            _passwordTextBox.Enabled = true;
            _displayNameTextBox.Enabled = true;
            _statusLabel.Text = "Status: Stopped.";
            _clientLabel.Text = "Viewer: No viewer connected.";
            _displayLabel.Text = "Display: " + GetDisplaySummary();
            _ipTextBox.Text = BuildLocalIpText();
        }

        private void UpdateStatus(string text)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(UpdateStatus), text);
                return;
            }

            _statusLabel.Text = "Status: " + text;
        }

        private void UpdateClient(string text)
        {
            if (IsDisposed)
            {
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(UpdateClient), text);
                return;
            }

            _clientLabel.Text = "Viewer: " + text;
        }

        private static string BuildLocalIpText()
        {
            var addresses = GetLocalIpv4Addresses();
            if (addresses.Count == 0)
            {
                return "Local IPv4: Unavailable";
            }

            var builder = new StringBuilder();
            builder.AppendLine("Local IPv4 addresses:");

            foreach (var address in addresses)
            {
                builder.AppendLine(address);
            }

            return builder.ToString().TrimEnd();
        }

        private static string GetDisplaySummary()
        {
            var virtualBounds = SystemInformation.VirtualScreen;
            return Screen.AllScreens.Length + " screen(s), virtual desktop " + virtualBounds.Width + "x" + virtualBounds.Height + ".";
        }

        private static List<string> GetLocalIpv4Addresses()
        {
            var results = new List<string>();

            try
            {
                var adapters = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                foreach (var adapter in adapters)
                {
                    var addresses = adapter.GetIPProperties().UnicastAddresses
                        .Select(address => address.Address)
                        .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                        .Select(address => address.ToString());

                    foreach (var address in addresses)
                    {
                        if (!results.Contains(address))
                        {
                            results.Add(address);
                        }
                    }
                }
            }
            catch
            {
            }

            results.Sort(StringComparer.Ordinal);
            return results;
        }
    }
}
