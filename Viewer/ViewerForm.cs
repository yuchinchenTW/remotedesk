using System;
using System.Drawing;
using System.Windows.Forms;
using SimpleRemote.Shared;

namespace SimpleRemote.Viewer
{
    internal sealed class ViewerForm : Form
    {
        private readonly TextBox _hostTextBox;
        private readonly TextBox _portTextBox;
        private readonly TextBox _passwordTextBox;
        private readonly Button _connectButton;
        private readonly Button _disconnectButton;
        private readonly PictureBox _pictureBox;
        private readonly Label _statusLabel;
        private readonly Label _infoLabel;

        private RemoteViewerClient _client;
        private Bitmap _currentFrame;
        private Size _remoteSize;

        public ViewerForm()
        {
            Text = "Simple Remote Viewer";
            Width = 1120;
            Height = 760;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 84
            };

            var hostLabel = new Label
            {
                Left = 12,
                Top = 14,
                Width = 48,
                Text = "Host"
            };

            _hostTextBox = new TextBox
            {
                Left = 58,
                Top = 10,
                Width = 170,
                Text = "127.0.0.1"
            };

            var portLabel = new Label
            {
                Left = 244,
                Top = 14,
                Width = 38,
                Text = "Port"
            };

            _portTextBox = new TextBox
            {
                Left = 282,
                Top = 10,
                Width = 72,
                Text = "5901"
            };

            var passwordLabel = new Label
            {
                Left = 370,
                Top = 14,
                Width = 66,
                Text = "Password"
            };

            _passwordTextBox = new TextBox
            {
                Left = 438,
                Top = 10,
                Width = 170,
                UseSystemPasswordChar = true,
                Text = "changeme"
            };

            _connectButton = new Button
            {
                Left = 626,
                Top = 8,
                Width = 100,
                Text = "Connect"
            };
            _connectButton.Click += ConnectButton_Click;

            _disconnectButton = new Button
            {
                Left = 734,
                Top = 8,
                Width = 100,
                Text = "Disconnect",
                Enabled = false
            };
            _disconnectButton.Click += DisconnectButton_Click;

            _statusLabel = new Label
            {
                Left = 12,
                Top = 46,
                Width = 1080,
                Height = 18,
                Text = "Status: Not connected."
            };

            _infoLabel = new Label
            {
                Left = 12,
                Top = 64,
                Width = 1080,
                Height = 18,
                Text = "Click inside the remote image before using the keyboard. This MVP controls only the primary monitor."
            };

            topPanel.Controls.Add(hostLabel);
            topPanel.Controls.Add(_hostTextBox);
            topPanel.Controls.Add(portLabel);
            topPanel.Controls.Add(_portTextBox);
            topPanel.Controls.Add(passwordLabel);
            topPanel.Controls.Add(_passwordTextBox);
            topPanel.Controls.Add(_connectButton);
            topPanel.Controls.Add(_disconnectButton);
            topPanel.Controls.Add(_statusLabel);
            topPanel.Controls.Add(_infoLabel);

            _pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom,
                TabStop = true
            };
            _pictureBox.MouseDown += PictureBox_MouseDown;
            _pictureBox.MouseUp += PictureBox_MouseUp;
            _pictureBox.MouseMove += PictureBox_MouseMove;
            _pictureBox.MouseWheel += PictureBox_MouseWheel;
            _pictureBox.MouseClick += PictureBox_MouseClick;

            Controls.Add(_pictureBox);
            Controls.Add(topPanel);

            KeyDown += ViewerForm_KeyDown;
            KeyUp += ViewerForm_KeyUp;
            FormClosed += ViewerForm_FormClosed;
        }

        private void ViewerForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (_client != null)
            {
                _client.Dispose();
            }

            if (_currentFrame != null)
            {
                _currentFrame.Dispose();
                _currentFrame = null;
            }
        }

        private void ConnectButton_Click(object sender, EventArgs e)
        {
            int port;
            if (!int.TryParse(_portTextBox.Text, out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, "Enter a valid TCP port.", "Invalid Port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _client = new RemoteViewerClient(UpdateStatus, UpdateFrame);
                _client.Connect(_hostTextBox.Text.Trim(), port, _passwordTextBox.Text);
                _connectButton.Enabled = false;
                _disconnectButton.Enabled = true;
                _hostTextBox.Enabled = false;
                _portTextBox.Enabled = false;
                _passwordTextBox.Enabled = false;
                _pictureBox.Focus();
            }
            catch (Exception ex)
            {
                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }

                MessageBox.Show(this, ex.Message, "Connection Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatus("Connection failed.");
            }
        }

        private void DisconnectButton_Click(object sender, EventArgs e)
        {
            ResetConnection();
        }

        private void PictureBox_MouseClick(object sender, MouseEventArgs e)
        {
            _pictureBox.Focus();
        }

        private void PictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            Point remotePoint;
            if (!TryTranslatePoint(e.Location, out remotePoint))
            {
                return;
            }

            if (_client != null)
            {
                _client.SendMouseMove(remotePoint.X, remotePoint.Y);
            }
        }

        private void PictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            _pictureBox.Focus();

            Point remotePoint;
            if (!TryTranslatePoint(e.Location, out remotePoint))
            {
                return;
            }

            if (_client != null)
            {
                _client.SendMouseButton(remotePoint.X, remotePoint.Y, TranslateButton(e.Button), true);
            }
        }

        private void PictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            Point remotePoint;
            if (!TryTranslatePoint(e.Location, out remotePoint))
            {
                return;
            }

            if (_client != null)
            {
                _client.SendMouseButton(remotePoint.X, remotePoint.Y, TranslateButton(e.Button), false);
            }
        }

        private void PictureBox_MouseWheel(object sender, MouseEventArgs e)
        {
            Point remotePoint;
            if (!TryTranslatePoint(e.Location, out remotePoint))
            {
                return;
            }

            if (_client != null)
            {
                _client.SendMouseWheel(remotePoint.X, remotePoint.Y, e.Delta);
            }
        }

        private void ViewerForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (_client != null && _pictureBox.Focused)
            {
                _client.SendKey(e.KeyValue, true);
                e.Handled = true;
            }
        }

        private void ViewerForm_KeyUp(object sender, KeyEventArgs e)
        {
            if (_client != null && _pictureBox.Focused)
            {
                _client.SendKey(e.KeyValue, false);
                e.Handled = true;
            }
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

            if (text == "Disconnected.")
            {
                ResetConnectionUiOnly();
            }
        }

        private void UpdateFrame(Bitmap frame, int width, int height)
        {
            if (IsDisposed)
            {
                frame.Dispose();
                return;
            }

            if (InvokeRequired)
            {
                BeginInvoke(new Action<Bitmap, int, int>(UpdateFrame), frame, width, height);
                return;
            }

            var previous = _currentFrame;
            _currentFrame = frame;
            _remoteSize = new Size(width, height);
            _pictureBox.Image = _currentFrame;

            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void ResetConnection()
        {
            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }

            ResetConnectionUiOnly();
            UpdateStatus("Disconnected.");
        }

        private void ResetConnectionUiOnly()
        {
            _connectButton.Enabled = true;
            _disconnectButton.Enabled = false;
            _hostTextBox.Enabled = true;
            _portTextBox.Enabled = true;
            _passwordTextBox.Enabled = true;
        }

        private bool TryTranslatePoint(Point localPoint, out Point remotePoint)
        {
            remotePoint = Point.Empty;

            if (_remoteSize.Width <= 0 || _remoteSize.Height <= 0 || _pictureBox.Width <= 0 || _pictureBox.Height <= 0)
            {
                return false;
            }

            var imageRect = GetImageRectangle(_pictureBox.ClientSize, _remoteSize);
            if (!imageRect.Contains(localPoint))
            {
                return false;
            }

            var xRatio = (double)(localPoint.X - imageRect.X) / imageRect.Width;
            var yRatio = (double)(localPoint.Y - imageRect.Y) / imageRect.Height;

            var remoteX = Math.Max(0, Math.Min(_remoteSize.Width - 1, (int)Math.Round(xRatio * (_remoteSize.Width - 1))));
            var remoteY = Math.Max(0, Math.Min(_remoteSize.Height - 1, (int)Math.Round(yRatio * (_remoteSize.Height - 1))));

            remotePoint = new Point(remoteX, remoteY);
            return true;
        }

        private static Rectangle GetImageRectangle(Size box, Size image)
        {
            var imageAspect = (double)image.Width / image.Height;
            var boxAspect = (double)box.Width / box.Height;

            if (boxAspect > imageAspect)
            {
                var height = box.Height;
                var width = (int)Math.Round(height * imageAspect);
                var x = (box.Width - width) / 2;
                return new Rectangle(x, 0, Math.Max(1, width), Math.Max(1, height));
            }

            var finalWidth = box.Width;
            var finalHeight = (int)Math.Round(finalWidth / imageAspect);
            var y = (box.Height - finalHeight) / 2;
            return new Rectangle(0, y, Math.Max(1, finalWidth), Math.Max(1, finalHeight));
        }

        private static MouseButtonCode TranslateButton(MouseButtons button)
        {
            switch (button)
            {
                case MouseButtons.Right:
                    return MouseButtonCode.Right;
                case MouseButtons.Middle:
                    return MouseButtonCode.Middle;
                default:
                    return MouseButtonCode.Left;
            }
        }
    }
}
