// PhotoPanelTab.cs — плагин Mission Planner "PhotoPanel 1.21_10.07.2026 (tab)" (для MP 1.3.83)
// Вкладка "Фото" на экране Flight Data (внизу слева, рядом с Quick/Actions/...):
// счётчик фотоснимков, телеметрия, средние интервалы съёмки.
// Установка: положить в C:\Program Files (x86)\Mission Planner\plugins\ и перезапустить MP.
// ВАЖНО: не держать в plugins одновременно с PhotoPanel.cs (оконной версией) —
// классы конфликтуют по имени namespace/подпискам, оставь один файл.

using System;
using System.Drawing;
using System.Windows.Forms;
using MissionPlanner;
using MissionPlanner.Plugin;

namespace PhotoPanelTabPlugin
{
    public class PhotoPanelTab : Plugin
    {
        private TabPage _tab;
        private Label _lblCount;
        private Label _lblPhotoTime;
        private Label _lblLastPhoto;
        private Label _lblAlt;
        private Label _lblSpeed;
        private Label _lblSats;
        private Label _lblAvgTime;
        private Label _lblAvgDist;
        private Label _lblCurWp;
        private Label _lblWpDist;

        // счётчик и статистика фото
        private int _photoCount = 0;
        private double _lastLat = 0, _lastLng = 0;
        private double _lastAlt = 0;
        private DateTime _lastPhotoTime = DateTime.MinValue;
        private double _sumDtSec = 0;
        private double _sumDistM = 0;
        private int _intervals = 0;
        private double _vdop = -1;
        private object _lock = new object();

        public override string Name { get { return "Photo Panel Tab"; } }
        public override string Version { get { return "1.21_10.07.2026"; } }
        public override string Author { get { return "andrewkena"; } }

        public override bool Init()
        {
            loopratehz = 2f;
            return true;
        }

        public override bool Loaded()
        {
            try
            {
                // CAMERA_FEEDBACK — счётчик фото + статистика интервалов
                Host.comPort.SubscribeToPacketType(
                    MAVLink.MAVLINK_MSG_ID.CAMERA_FEEDBACK,
                    delegate(MAVLink.MAVLinkMessage message)
                    {
                        MAVLink.mavlink_camera_feedback_t fb =
                            (MAVLink.mavlink_camera_feedback_t)message.data;
                        double lat = fb.lat / 10000000.0;
                        double lng = fb.lng / 10000000.0;
                        DateTime now = DateTime.Now;

                        lock (_lock)
                        {
                            if (_photoCount > 0)
                            {
                                _sumDtSec += (now - _lastPhotoTime).TotalSeconds;
                                _sumDistM += DistM(_lastLat, _lastLng, lat, lng);
                                _intervals++;
                            }
                            _photoCount++;
                            _lastLat = lat;
                            _lastLng = lng;
                            _lastAlt = fb.alt_rel;
                            _lastPhotoTime = now;
                        }
                        return true;
                    },
                    (byte)Host.comPort.sysidcurrent,
                    (byte)Host.comPort.compidcurrent,
                    false);

                // GPS_RAW_INT — VDOP (epv)
                Host.comPort.SubscribeToPacketType(
                    MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT,
                    delegate(MAVLink.MAVLinkMessage message)
                    {
                        MAVLink.mavlink_gps_raw_int_t gps =
                            (MAVLink.mavlink_gps_raw_int_t)message.data;
                        lock (_lock)
                        {
                            _vdop = (gps.epv == 65535) ? -1 : gps.epv / 100.0;
                        }
                        return true;
                    },
                    (byte)Host.comPort.sysidcurrent,
                    (byte)Host.comPort.compidcurrent,
                    false);

                // Вкладка создаётся в главном потоке
                MainV2.instance.BeginInvoke((MethodInvoker)delegate
                {
                    AddTab();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("PhotoPanelTab Loaded error: " + ex.ToString());
            }

            return true;
        }

        public override bool Loop()
        {
            if (_tab == null || _tab.IsDisposed)
                return true;

            try
            {
                MainV2.instance.BeginInvoke((MethodInvoker)delegate
                {
                    EnsureTabAttached();
                    UpdateLabels();
                });
            }
            catch (Exception) { }

            return true;
        }

        // Mission Planner при подключении (например запуск SITL) вызывает
        // FlightData.Activate() -> updateDisplayView() -> loadTabControlActions(),
        // где tabControlactions.TabPages.Clear() сносит все вкладки и восстанавливает
        // только свой встроенный список — наша вкладка туда не входит и пропадает.
        // Поэтому на каждый тик проверяем и возвращаем её обратно.
        private void EnsureTabAttached()
        {
            Control[] found = MainV2.instance.FlightData.Controls.Find("tabControlactions", true);
            if (found.Length == 0)
                return;

            TabControl tc = (TabControl)found[0];
            if (!tc.TabPages.Contains(_tab))
                tc.TabPages.Add(_tab);
        }

        public override bool Exit()
        {
            return true;
        }

        // ---------- UI (только главный поток) ----------

        private void AddTab()
        {
            Control[] found = MainV2.instance.FlightData.Controls.Find("tabControlactions", true);
            if (found.Length == 0)
            {
                Console.WriteLine("PhotoPanelTab: tabControlactions не найден");
                return;
            }
            TabControl tc = (TabControl)found[0];

            _tab = new TabPage("PhotoPanel");
            _tab.BackColor = Color.FromArgb(38, 39, 40);
            _tab.AutoScroll = true; // если блок ниже по высоте — появится прокрутка

            Font big = new Font("Segoe UI", 22, FontStyle.Bold);
            Font normal = new Font("Segoe UI", 10);

            int y = 10;

            Label caption = MakeLabel("Снимков сделано:", normal, 10, y);
            _lblCount = MakeLabel("0", big, 10, y + 22);
            _lblCount.ForeColor = Color.LimeGreen;

            Button btnReset = new Button();
            btnReset.Text = "Сброс";
            btnReset.Location = new Point(240, y + 28);
            btnReset.Size = new Size(90, 30);
            btnReset.FlatStyle = FlatStyle.Flat;
            btnReset.ForeColor = Color.WhiteSmoke;
            btnReset.Click += delegate
            {
                lock (_lock)
                {
                    _photoCount = 0;
                    _lastPhotoTime = DateTime.MinValue;
                    _lastAlt = 0;
                    _sumDtSec = 0;
                    _sumDistM = 0;
                    _intervals = 0;
                }
            };

            y += 70;
            Label line1 = MakeLine(y);

            y += 10;
            _lblPhotoTime = MakeLabel("Время: —", normal, 10, y);
            y += 25;
            _lblLastPhoto = MakeLabel("Последнее фото: —", normal, 10, y);
            y += 25;
            _lblAlt = MakeLabel("Высота: —", normal, 10, y);
            y += 25;
            _lblSpeed = MakeLabel("Скорость: —", normal, 10, y);

            y += 30;
            Label line2 = MakeLine(y);

            y += 10;
            _lblSats = MakeLabel("Спутники: —", normal, 10, y);

            y += 30;
            Label line3 = MakeLine(y);

            y += 10;
            _lblAvgTime = MakeLabel("Среднее время между фотографиями: —", normal, 10, y);
            y += 25;
            _lblAvgDist = MakeLabel("Среднее расстояние между фотографиями: —", normal, 10, y);

            y += 30;
            Label line4 = MakeLine(y);

            y += 10;
            _lblCurWp = MakeLabel("Следующий WP: —", normal, 10, y);
            y += 25;
            _lblWpDist = MakeLabel("Расстояние до следующего WP: —", normal, 10, y);

            Label lineV = MakeVLine(355, 10, 340);

            int ry = 10;
            Button btnStartTime = new Button();
            btnStartTime.Text = "Фото по времени";
            btnStartTime.Location = new Point(370, ry);
            btnStartTime.Size = new Size(170, 30);
            btnStartTime.FlatStyle = FlatStyle.Flat;
            btnStartTime.ForeColor = Color.WhiteSmoke;

            NumericUpDown nudTime = new NumericUpDown();
            nudTime.Location = new Point(370, ry + 36);
            nudTime.Size = new Size(70, 24);
            nudTime.DecimalPlaces = 1;
            nudTime.Increment = 0.1M;
            nudTime.Minimum = 1M;
            nudTime.Maximum = 300M;
            nudTime.Value = 1.5M;

            Label lblTimeUnit = MakeLabel("с", normal, 448, ry + 39);

            btnStartTime.Click += delegate
            {
                try
                {
                    Host.comPort.doCommand((byte)Host.comPort.sysidcurrent, (byte)Host.comPort.compidcurrent,
                        MAVLink.MAV_CMD.IMAGE_START_CAPTURE, 0, (float)nudTime.Value, 0, 0, 0, 0, 0, false);
                }
                catch (Exception ex) { Console.WriteLine("PhotoPanel: старт по времени — " + ex.Message); }
            };

            ry += 76;
            Button btnStartDist = new Button();
            btnStartDist.Text = "Фото по расстоянию";
            btnStartDist.Location = new Point(370, ry);
            btnStartDist.Size = new Size(170, 30);
            btnStartDist.FlatStyle = FlatStyle.Flat;
            btnStartDist.ForeColor = Color.WhiteSmoke;

            NumericUpDown nudDist = new NumericUpDown();
            nudDist.Location = new Point(370, ry + 36);
            nudDist.Size = new Size(70, 24);
            nudDist.DecimalPlaces = 0;
            nudDist.Increment = 1M;
            nudDist.Minimum = 50M;
            nudDist.Maximum = 10000M;
            nudDist.Value = 50M;

            Label lblDistUnit = MakeLabel("м", normal, 448, ry + 39);

            btnStartDist.Click += delegate
            {
                try
                {
                    Host.comPort.doCommand((byte)Host.comPort.sysidcurrent, (byte)Host.comPort.compidcurrent,
                        MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST, (float)nudDist.Value, 0, 0, 0, 0, 0, 0, false);
                }
                catch (Exception ex) { Console.WriteLine("PhotoPanel: старт по расстоянию — " + ex.Message); }
            };

            ry += 76;
            Button btnStop = new Button();
            btnStop.Text = "Остановить фото";
            btnStop.Location = new Point(370, ry);
            btnStop.Size = new Size(170, 40);
            btnStop.FlatStyle = FlatStyle.Flat;
            btnStop.BackColor = Color.Red;
            btnStop.ForeColor = Color.White;
            btnStop.Font = new Font(normal, FontStyle.Bold);
            btnStop.Click += delegate
            {
                try
                {
                    Host.comPort.doCommand((byte)Host.comPort.sysidcurrent, (byte)Host.comPort.compidcurrent,
                        MAVLink.MAV_CMD.IMAGE_STOP_CAPTURE, 0, 0, 0, 0, 0, 0, 0, false);
                    Host.comPort.doCommand((byte)Host.comPort.sysidcurrent, (byte)Host.comPort.compidcurrent,
                        MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST, 0, 0, 0, 0, 0, 0, 0, false);
                }
                catch (Exception ex) { Console.WriteLine("PhotoPanel: остановка — " + ex.Message); }
            };

            Label lblVersion = MakeLabel("v" + Version, new Font("Segoe UI", 8), 10, 272);
            lblVersion.ForeColor = Color.Gray;

            _tab.Controls.Add(caption);
            _tab.Controls.Add(_lblCount);
            _tab.Controls.Add(btnReset);
            _tab.Controls.Add(line1);
            _tab.Controls.Add(_lblPhotoTime);
            _tab.Controls.Add(_lblLastPhoto);
            _tab.Controls.Add(_lblAlt);
            _tab.Controls.Add(_lblSpeed);
            _tab.Controls.Add(line2);
            _tab.Controls.Add(_lblSats);
            _tab.Controls.Add(line3);
            _tab.Controls.Add(_lblAvgTime);
            _tab.Controls.Add(_lblAvgDist);
            _tab.Controls.Add(line4);
            _tab.Controls.Add(_lblCurWp);
            _tab.Controls.Add(_lblWpDist);
            _tab.Controls.Add(lineV);
            _tab.Controls.Add(btnStartTime);
            _tab.Controls.Add(nudTime);
            _tab.Controls.Add(lblTimeUnit);
            _tab.Controls.Add(btnStartDist);
            _tab.Controls.Add(nudDist);
            _tab.Controls.Add(lblDistUnit);
            _tab.Controls.Add(btnStop);
            _tab.Controls.Add(lblVersion);

            tc.TabPages.Add(_tab);
        }

        private Label MakeLine(int y)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.Height = 2;
            l.Width = 335;
            l.Location = new Point(10, y);
            l.BorderStyle = BorderStyle.Fixed3D;
            return l;
        }

        private Label MakeVLine(int x, int y, int height)
        {
            Label l = new Label();
            l.AutoSize = false;
            l.Width = 2;
            l.Height = height;
            l.Location = new Point(x, y);
            l.BorderStyle = BorderStyle.Fixed3D;
            return l;
        }

        private Label MakeLabel(string text, Font font, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = font;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            l.ForeColor = Color.WhiteSmoke;
            l.BackColor = Color.Transparent;
            return l;
        }

        private void UpdateLabels()
        {
            if (_lblCount == null) return;

            int count, intervals;
            double lat, lng, alt, sumDt, sumDist, vdop;
            DateTime t;
            lock (_lock)
            {
                count = _photoCount;
                lat = _lastLat;
                lng = _lastLng;
                alt = _lastAlt;
                t = _lastPhotoTime;
                sumDt = _sumDtSec;
                sumDist = _sumDistM;
                intervals = _intervals;
                vdop = _vdop;
            }

            _lblCount.Text = count.ToString();

            CurrentState cs = Host.cs;
            _lblPhotoTime.Text = string.Format("Время: {0}", FormatFlightTime(cs.timeInAir));

            if (t == DateTime.MinValue)
            {
                _lblLastPhoto.Text = "Последнее фото: —";
            }
            else
            {
                _lblLastPhoto.Text = string.Format("Последнее фото: {0:F6}, {1:F6}", lat, lng);
            }

            if (t == DateTime.MinValue)
                _lblAlt.Text = "Высота последнего снимка: —";
            else
                _lblAlt.Text = string.Format("Высота последнего снимка: {0:F1} м (отн.)", alt);

            _lblSpeed.Text = string.Format("Скорость: {0:F1} м/с", cs.airspeed);

            string vdopText = (vdop < 0) ? "—" : vdop.ToString("F1");
            _lblSats.Text = string.Format("Спутники: {0}   HDOP: {1:F1}   VDOP: {2}",
                cs.satcount, cs.gpshdop, vdopText);

            if (intervals > 0)
            {
                _lblAvgTime.Text = string.Format(
                    "Среднее время между фотографиями: {0:F1} с", sumDt / intervals);
                _lblAvgDist.Text = string.Format(
                    "Среднее расстояние между фотографиями: {0:F1} м", sumDist / intervals);
            }
            else
            {
                _lblAvgTime.Text = "Среднее время между фотографиями: —";
                _lblAvgDist.Text = "Среднее расстояние между фотографиями: —";
            }

            _lblCurWp.Text = string.Format("Следующий WP: {0}", cs.wpno);
            _lblWpDist.Text = string.Format("Расстояние до следующего WP: {0:F0} м", cs.wp_dist);
        }

        // Время полёта (Host.cs.timeInAir, сек) в формате ЧЧ:ММ:СС
        private static string FormatFlightTime(float seconds)
        {
            if (seconds < 0) seconds = 0;
            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:00}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
        }

        // Расстояние между двумя точками (гаверсинус), метры
        private static double DistM(double lat1, double lon1, double lat2, double lon2)
        {
            double R = 6371000.0;
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
        }
    }
}