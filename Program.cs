using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HardwareInspector
{
    public class HardwareItem
    {
        public string ID { get; set; }
        public string Category { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public string Source { get; set; }
        public string Details { get; set; }
    }

    public static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetSystemFirmwareTable(uint FirmwareTableProviderSignature, uint FirmwareTableID, IntPtr pFirmwareTableBuffer, uint BufferSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1;
        public const uint FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        [StructLayout(LayoutKind.Sequential)]
        public struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;
            public uint QueryType;
            public byte AdditionalParameters;
        }
    }

    public static class HardwareEngine
    {
        public static List<HardwareItem> Collect()
        {
            var list = new List<HardwareItem>();
            var smbios = ReadSMBIOS();

            string mbSerial = smbios.ContainsKey("BaseBoard_Serial") ? smbios["BaseBoard_Serial"] : QueryWMI("Win32_BaseBoard", "SerialNumber");
            list.Add(new HardwareItem
            {
                ID = "mb_serial",
                Category = "Motherboard",
                Name = "Serial",
                Value = CleanSerial(Fallback(mbSerial, "N/A")),
                Source = "SMBIOS Type 2",
                Details = (QueryWMI("Win32_BaseBoard", "Manufacturer") + " " + QueryWMI("Win32_BaseBoard", "Product")).Trim()
            });

            string biosSerial = smbios.ContainsKey("System_Serial") ? smbios["System_Serial"] : QueryWMI("Win32_BIOS", "SerialNumber");
            list.Add(new HardwareItem
            {
                ID = "bios_serial",
                Category = "BIOS",
                Name = "Serial",
                Value = CleanSerial(Fallback(biosSerial, "N/A")),
                Source = "SMBIOS Type 1",
                Details = "American Megatrends | Ver: " + QueryWMI("Win32_BIOS", "SMBIOSBIOSVersion")
            });

            string cpuId = smbios.ContainsKey("Processor_ID") ? smbios["Processor_ID"] : QueryWMI("Win32_Processor", "ProcessorId");
            list.Add(new HardwareItem
            {
                ID = "cpu_id",
                Category = "CPU",
                Name = "Processor ID",
                Value = CleanSerial(Fallback(cpuId, "N/A")),
                Source = "Win32_Processor / SMBIOS Type 4",
                Details = QueryWMI("Win32_Processor", "Name")
            });

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Index, Model, SerialNumber FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string idxStr = mo["Index"] != null ? mo["Index"].ToString() : "0";
                        int idx = 0;
                        int.TryParse(idxStr, out idx);
                        string model = mo["Model"] != null ? mo["Model"].ToString().Trim() : "";
                        string serial = mo["SerialNumber"] != null ? mo["SerialNumber"].ToString().Trim() : "";

                        string prod;
                        string ioctlSerial = ReadDiskSerialIOCTL(idx, out prod);
                        if (!string.IsNullOrEmpty(ioctlSerial))
                        {
                            serial = ioctlSerial;
                        }

                        if (!string.IsNullOrEmpty(serial))
                        {
                            list.Add(new HardwareItem
                            {
                                ID = "disk_" + idxStr,
                                Category = "Disk",
                                Name = "Serial",
                                Value = model + ": " + serial,
                                Source = "IOCTL_STORAGE_QUERY_PROPERTY / Win32_DiskDrive",
                                Details = model
                            });
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_VideoController"))
                {
                    int gpuIdx = 0;
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string gName = mo["Name"] != null ? mo["Name"].ToString() : "";
                        string pnp = mo["PNPDeviceID"] != null ? mo["PNPDeviceID"].ToString() : "";
                        list.Add(new HardwareItem
                        {
                            ID = "gpu_" + gpuIdx++,
                            Category = "GPU",
                            Name = "Serial",
                            Value = ExtractInstance(pnp),
                            Source = "Win32_VideoController / PCIe",
                            Details = gName
                        });
                    }
                }
            }
            catch { }

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT ClassGuid, Service, Name, DeviceID FROM Win32_PnPEntity"))
                {
                    int pIdx = 0;
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string devId = mo["DeviceID"] != null ? mo["DeviceID"].ToString() : "";
                        string name = mo["Name"] != null ? mo["Name"].ToString() : "";
                        string guid = mo["ClassGuid"] != null ? mo["ClassGuid"].ToString().ToLower() : "";

                        string cat = "";

                        if (guid == "{4d36e96b-e325-11ce-bfc1-08002be10318}" || devId.StartsWith(@"HID\VID") && name.ToLower().Contains("keyboard"))
                        {
                            cat = "Keyboard";
                        }
                        else if (guid == "{4d36e96f-e325-11ce-bfc1-08002be10318}" || devId.StartsWith(@"HID\VID") && name.ToLower().Contains("mouse"))
                        {
                            cat = "Mouse";
                        }
                        else if (guid == "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}" || devId.StartsWith(@"USB\VID") && name.ToLower().Contains("bluetooth"))
                        {
                            cat = "Bluetooth";
                        }
                        else if (guid == "{36fc9e60-c465-11cf-8056-444553540000}" || devId.StartsWith(@"USB\ROOT_HUB") || devId.StartsWith(@"USB\VID") && name.ToLower().Contains("hub") || name.ToLower().Contains("composite"))
                        {
                            cat = "USB Hub";
                        }
                        else if (guid == "{745a17a0-74d3-11d0-b6fe-00a0c90f57da}" || devId.StartsWith(@"HID\"))
                        {
                            cat = "HID Device";
                        }

                        if (!string.IsNullOrEmpty(cat))
                        {
                            string val = ExtractInstance(devId);
                            if (!string.IsNullOrEmpty(val))
                            {
                                list.Add(new HardwareItem
                                {
                                    ID = "pnp_" + pIdx++,
                                    Category = cat,
                                    Name = "Serial",
                                    Value = val,
                                    Source = "Win32_PnPEntity",
                                    Details = name
                                });
                            }
                        }
                    }
                }
            }
            catch { }

            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                int nicCounter = 0;
                foreach (NetworkInterface nic in nics)
                {
                    byte[] macBytes = nic.GetPhysicalAddress().GetAddressBytes();
                    if (macBytes != null && macBytes.Length > 0)
                    {
                        string macStr = string.Join(":", macBytes.Select(b => b.ToString("X2")));
                        if (!string.IsNullOrEmpty(macStr) && macStr != "00:00:00:00:00:00")
                        {
                            list.Add(new HardwareItem
                            {
                                ID = "nic_" + nicCounter++,
                                Category = "Network Adapter",
                                Name = "MAC Address",
                                Value = macStr,
                                Source = "NetworkInterface",
                                Details = nic.Description
                            });
                        }
                    }
                }
            }
            catch { }

            ReadRegistryFingerprints(list);

            list.Sort((a, b) =>
            {
                int orderA = GetCategoryOrder(a.Category);
                int orderB = GetCategoryOrder(b.Category);
                if (orderA != orderB) return orderA.CompareTo(orderB);
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return list;
        }

        private static int GetCategoryOrder(string cat)
        {
            switch (cat)
            {
                case "Motherboard": return 1;
                case "BIOS": return 2;
                case "CPU": return 3;
                case "Disk": return 4;
                case "GPU": return 5;
                case "Keyboard": return 6;
                case "Mouse": return 7;
                case "Bluetooth": return 8;
                case "USB Hub": return 9;
                case "HID Device": return 10;
                case "Network Adapter": return 11;
                case "Registry Fingerprint": return 12;
                default: return 99;
            }
        }

        private static void ReadRegistryFingerprints(List<HardwareItem> list)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("MachineGuid");
                        if (val != null)
                        {
                            list.Add(new HardwareItem
                            {
                                ID = "reg_machine_guid",
                                Category = "Registry Fingerprint",
                                Name = "MachineGuid",
                                Value = val.ToString(),
                                Source = @"HKLM\SOFTWARE\Microsoft\Cryptography",
                                Details = "Windows Cryptography Machine GUID"
                            });
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\IDConfigDB\Hardware Profiles\0001"))
                {
                    if (key != null)
                    {
                        object hProfile = key.GetValue("HwProfileGuid");
                        if (hProfile != null)
                        {
                            list.Add(new HardwareItem
                            {
                                ID = "reg_hw_profile_guid",
                                Category = "Registry Fingerprint",
                                Name = "HwProfileGuid",
                                Value = hProfile.ToString(),
                                Source = @"HKLM\SYSTEM\CurrentControlSet\Control\IDConfigDB",
                                Details = "Hardware Profile GUID"
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private static string ReadDiskSerialIOCTL(int driveIndex, out string prodName)
        {
            prodName = "";
            string path = @"\\.\PhysicalDrive" + driveIndex;
            IntPtr handle = NativeMethods.CreateFile(path, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle == (IntPtr)(-1) || handle == IntPtr.Zero)
            {
                handle = NativeMethods.CreateFile(path, 0, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle == (IntPtr)(-1) || handle == IntPtr.Zero) return null;
            }

            try
            {
                NativeMethods.STORAGE_PROPERTY_QUERY query = new NativeMethods.STORAGE_PROPERTY_QUERY { PropertyId = 0, QueryType = 0 };
                int inSize = Marshal.SizeOf(typeof(NativeMethods.STORAGE_PROPERTY_QUERY));
                IntPtr inBuf = Marshal.AllocHGlobal(inSize);
                Marshal.StructureToPtr(query, inBuf, false);

                int outSize = 2048;
                IntPtr outBuf = Marshal.AllocHGlobal(outSize);
                uint returned = 0;

                bool ok = NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, inBuf, (uint)inSize, outBuf, (uint)outSize, out returned, IntPtr.Zero);
                Marshal.FreeHGlobal(inBuf);

                if (!ok)
                {
                    Marshal.FreeHGlobal(outBuf);
                    return null;
                }

                uint prodOffset = (uint)Marshal.ReadInt32(outBuf, 16);
                uint serialOffset = (uint)Marshal.ReadInt32(outBuf, 24);

                string serial = "";
                if (serialOffset > 0 && serialOffset < outSize)
                {
                    serial = Marshal.PtrToStringAnsi(new IntPtr(outBuf.ToInt64() + serialOffset)).Trim();
                }

                if (prodOffset > 0 && prodOffset < outSize)
                {
                    prodName = Marshal.PtrToStringAnsi(new IntPtr(outBuf.ToInt64() + prodOffset)).Trim();
                }

                Marshal.FreeHGlobal(outBuf);
                return string.IsNullOrEmpty(serial) ? null : serial;
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        private static Dictionary<string, string> ReadSMBIOS()
        {
            var dict = new Dictionary<string, string>();
            uint sig = 0x52534D42;
            uint size = NativeMethods.GetSystemFirmwareTable(sig, 0, IntPtr.Zero, 0);
            if (size == 0) return dict;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            uint ret = NativeMethods.GetSystemFirmwareTable(sig, 0, buf, size);
            if (ret == 0)
            {
                Marshal.FreeHGlobal(buf);
                return dict;
            }

            byte[] raw = new byte[size];
            Marshal.Copy(buf, raw, 0, (int)size);
            Marshal.FreeHGlobal(buf);

            if (raw.Length < 8) return dict;

            int idx = 8;
            while (idx + 4 <= raw.Length)
            {
                byte hType = raw[idx];
                int hLen = raw[idx + 1];
                if (hLen < 4 || idx + hLen > raw.Length) break;

                int strIdx = idx + hLen;
                var strList = new List<string>();
                while (strIdx < raw.Length)
                {
                    if (raw[strIdx] == 0)
                    {
                        if (strIdx + 1 < raw.Length && raw[strIdx + 1] == 0)
                        {
                            strIdx += 2;
                            break;
                        }
                        strIdx++;
                        continue;
                    }
                    int end = Array.IndexOf(raw, (byte)0, strIdx);
                    if (end == -1) break;
                    string s = Encoding.ASCII.GetString(raw, strIdx, end - strIdx).Trim();
                    strList.Add(s);
                    strIdx = end + 1;
                }

                Func<byte, string> getStr = b =>
                {
                    if (b == 0 || b > strList.Count) return "";
                    return strList[b - 1];
                };

                if (hType == 0 && hLen >= 18)
                {
                    dict["BIOS_Vendor"] = getStr(raw[idx + 4]);
                    dict["BIOS_Version"] = getStr(raw[idx + 5]);
                }
                else if (hType == 1 && hLen >= 25)
                {
                    dict["System_Manufacturer"] = getStr(raw[idx + 4]);
                    dict["System_ProductName"] = getStr(raw[idx + 5]);
                    dict["System_Serial"] = getStr(raw[idx + 7]);
                }
                else if (hType == 2 && hLen >= 8)
                {
                    dict["BaseBoard_Manufacturer"] = getStr(raw[idx + 4]);
                    dict["BaseBoard_Product"] = getStr(raw[idx + 5]);
                    dict["BaseBoard_Serial"] = getStr(raw[idx + 7]);
                }
                else if (hType == 4 && hLen >= 16)
                {
                    ulong cpuid = BitConverter.ToUInt64(raw, idx + 8);
                    dict["Processor_ID"] = cpuid.ToString("X16");
                }

                idx = strIdx;
            }

            return dict;
        }

        private static string QueryWMI(string table, string prop)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT " + prop + " FROM " + table))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        if (mo[prop] != null) return mo[prop].ToString().Trim();
                    }
                }
            }
            catch { }
            return "";
        }

        private static string ExtractInstance(string pnp)
        {
            if (string.IsNullOrEmpty(pnp)) return "";
            string[] parts = pnp.Split('\\');
            if (parts.Length > 0)
            {
                string last = parts[parts.Length - 1];
                if (last.Contains("&") || last.Length > 3) return last;
            }
            return pnp;
        }

        private static string CleanSerial(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "N/A";
            return s.Trim();
        }

        private static string Fallback(string val, string def)
        {
            if (string.IsNullOrWhiteSpace(val) || val == "<nil>") return def;
            return val.Trim();
        }
    }

    public class MainForm : Form
    {
        private ListView lvLive;
        private ListView lvDiff;
        private TextBox txtSearch;
        private ComboBox cmbCategory;
        private ComboBox cmbSnapshots;
        private Button btnDiff;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel lblStatus;
        private TabControl tabControl;

        private List<HardwareItem> currentList = new List<HardwareItem>();
        private List<List<HardwareItem>> snapshots = new List<List<HardwareItem>>();
        private List<string> snapshotLabels = new List<string>();

        public MainForm()
        {
            InitializeComponent();
            LoadData();
        }

        private void InitializeComponent()
        {
            this.Text = "Hardware Inspector";
            this.Size = new Size(1180, 750);
            this.MinimumSize = new Size(950, 550);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            ToolStrip toolStrip = new ToolStrip();
            toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            toolStrip.RenderMode = ToolStripRenderMode.System;
            toolStrip.Padding = new Padding(6, 4, 6, 4);

            ToolStripLabel lblSearch = new ToolStripLabel("Search:");
            ToolStripTextBox searchBox = new ToolStripTextBox { Width = 220 };
            this.txtSearch = searchBox.TextBox;
            this.txtSearch.TextChanged += (s, e) => FilterLiveList();

            ToolStripLabel lblCat = new ToolStripLabel("  Category:");
            ToolStripComboBox catCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
            this.cmbCategory = catCombo.ComboBox;
            this.cmbCategory.SelectedIndexChanged += (s, e) => FilterLiveList();

            ToolStripSeparator sep1 = new ToolStripSeparator();
            ToolStripButton tsbScan = new ToolStripButton("Scan Now");
            tsbScan.Click += (s, e) => LoadData();

            ToolStripButton tsbBaseline = new ToolStripButton("Set Baseline");
            tsbBaseline.Click += (s, e) => TakeSnapshot();

            ToolStripButton tsbCopy = new ToolStripButton("Copy");
            tsbCopy.Click += (s, e) => CopySelected();

            ToolStripButton tsbExport = new ToolStripButton("Export");
            tsbExport.Click += (s, e) => ExportData();

            toolStrip.Items.AddRange(new ToolStripItem[] {
                lblSearch, searchBox,
                lblCat, catCombo,
                sep1,
                tsbScan, tsbBaseline, tsbCopy, tsbExport
            });

            tabControl = new TabControl { Dock = DockStyle.Fill };

            TabPage tabLive = new TabPage("Inventory");
            lvLive = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false
            };
            lvLive.Columns.Add("Category", 130);
            lvLive.Columns.Add("Name", 110);
            lvLive.Columns.Add("Value", 450);
            lvLive.Columns.Add("Source Layer", 220);
            lvLive.Columns.Add("Hardware Details", 250);
            tabLive.Controls.Add(lvLive);

            TabPage tabDiff = new TabPage("Before / After Comparison");
            Panel diffPanel = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8) };
            Label lblDiff = new Label { Text = "Baseline:", Location = new Point(8, 12), AutoSize = true };
            cmbSnapshots = new ComboBox { Location = new Point(70, 8), Width = 360, DropDownStyle = ComboBoxStyle.DropDownList };
            btnDiff = new Button { Text = "Compare", Location = new Point(440, 7), Size = new Size(90, 26) };
            btnDiff.Click += (s, e) => RunDiff();
            diffPanel.Controls.AddRange(new Control[] { lblDiff, cmbSnapshots, btnDiff });

            lvDiff = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = false
            };
            lvDiff.Columns.Add("Status", 110);
            lvDiff.Columns.Add("Category", 130);
            lvDiff.Columns.Add("Name", 110);
            lvDiff.Columns.Add("Baseline (Before)", 390);
            lvDiff.Columns.Add("Current State (After)", 390);

            tabDiff.Controls.Add(lvDiff);
            tabDiff.Controls.Add(diffPanel);

            tabControl.TabPages.Add(tabLive);
            tabControl.TabPages.Add(tabDiff);

            statusStrip = new StatusStrip();
            lblStatus = new ToolStripStatusLabel("Ready");
            statusStrip.Items.Add(lblStatus);

            this.Controls.Add(tabControl);
            this.Controls.Add(toolStrip);
            this.Controls.Add(statusStrip);
        }

        private void LoadData()
        {
            lblStatus.Text = "Scanning hardware...";
            this.Cursor = Cursors.WaitCursor;
            Task.Run(() =>
            {
                var items = HardwareEngine.Collect();
                this.Invoke(new Action(() =>
                {
                    currentList = items;
                    PopulateCategories();
                    FilterLiveList();
                    if (snapshots.Count == 0)
                    {
                        snapshots.Add(new List<HardwareItem>(items));
                        snapshotLabels.Add("Initial Baseline (" + DateTime.Now.ToLongTimeString() + ")");
                        UpdateSnapshotCombo();
                    }
                    lblStatus.Text = "Scan complete. " + items.Count + " identifiers detected.";
                    this.Cursor = Cursors.Default;
                }));
            });
        }

        private void PopulateCategories()
        {
            var cats = new HashSet<string> { "All" };
            foreach (var item in currentList)
            {
                cats.Add(item.Category);
            }
            string sel = cmbCategory.SelectedItem != null ? cmbCategory.SelectedItem.ToString() : "All";
            cmbCategory.Items.Clear();
            foreach (var c in cats) cmbCategory.Items.Add(c);
            cmbCategory.SelectedItem = cats.Contains(sel) ? sel : "All";
        }

        private void FilterLiveList()
        {
            lvLive.BeginUpdate();
            lvLive.Items.Clear();

            string search = txtSearch.Text.ToLower().Trim();
            string selectedCat = cmbCategory.SelectedItem != null ? cmbCategory.SelectedItem.ToString() : "All";

            foreach (var item in currentList)
            {
                bool matchCat = selectedCat == "All" || item.Category == selectedCat;
                bool matchSearch = string.IsNullOrEmpty(search) ||
                                   item.Category.ToLower().Contains(search) ||
                                   item.Name.ToLower().Contains(search) ||
                                   item.Value.ToLower().Contains(search) ||
                                   item.Details.ToLower().Contains(search);

                if (matchCat && matchSearch)
                {
                    var lvi = new ListViewItem(item.Category);
                    lvi.SubItems.Add(item.Name);
                    lvi.SubItems.Add(item.Value);
                    lvi.SubItems.Add(item.Source);
                    lvi.SubItems.Add(item.Details);
                    lvLive.Items.Add(lvi);
                }
            }
            lvLive.EndUpdate();
        }

        private void TakeSnapshot()
        {
            string label = "Baseline #" + (snapshots.Count + 1) + " (" + DateTime.Now.ToLongTimeString() + ")";
            snapshots.Add(new List<HardwareItem>(currentList));
            snapshotLabels.Add(label);
            UpdateSnapshotCombo();
            lblStatus.Text = "Saved baseline: " + label;
        }

        private void UpdateSnapshotCombo()
        {
            cmbSnapshots.Items.Clear();
            for (int i = 0; i < snapshotLabels.Count; i++)
            {
                cmbSnapshots.Items.Add(snapshotLabels[i] + " [" + snapshots[i].Count + " items]");
            }
            if (cmbSnapshots.Items.Count > 0) cmbSnapshots.SelectedIndex = 0;
        }

        private void RunDiff()
        {
            if (snapshots.Count == 0 || cmbSnapshots.SelectedIndex < 0) return;

            int idx = cmbSnapshots.SelectedIndex;
            var baseline = snapshots[idx];
            var baseDict = baseline.ToDictionary(k => k.ID, v => v);
            var curDict = currentList.ToDictionary(k => k.ID, v => v);

            lvDiff.BeginUpdate();
            lvDiff.Items.Clear();

            foreach (var cur in currentList)
            {
                if (baseDict.ContainsKey(cur.ID))
                {
                    var b = baseDict[cur.ID];
                    if (b.Value.Trim() != cur.Value.Trim())
                    {
                        var lvi = new ListViewItem("MODIFIED");
                        lvi.BackColor = Color.FromArgb(255, 243, 205);
                        lvi.SubItems.Add(cur.Category);
                        lvi.SubItems.Add(cur.Name);
                        lvi.SubItems.Add(b.Value);
                        lvi.SubItems.Add(cur.Value);
                        lvDiff.Items.Add(lvi);
                    }
                    else
                    {
                        var lvi = new ListViewItem("UNCHANGED");
                        lvi.SubItems.Add(cur.Category);
                        lvi.SubItems.Add(cur.Name);
                        lvi.SubItems.Add(b.Value);
                        lvi.SubItems.Add(cur.Value);
                        lvDiff.Items.Add(lvi);
                    }
                }
                else
                {
                    var lvi = new ListViewItem("ADDED");
                    lvi.BackColor = Color.FromArgb(209, 231, 221);
                    lvi.SubItems.Add(cur.Category);
                    lvi.SubItems.Add(cur.Name);
                    lvi.SubItems.Add("(None)");
                    lvi.SubItems.Add(cur.Value);
                    lvDiff.Items.Add(lvi);
                }
            }

            foreach (var b in baseline)
            {
                if (!curDict.ContainsKey(b.ID))
                {
                    var lvi = new ListViewItem("REMOVED");
                    lvi.BackColor = Color.FromArgb(248, 215, 220);
                    lvi.SubItems.Add(b.Category);
                    lvi.SubItems.Add(b.Name);
                    lvi.SubItems.Add(b.Value);
                    lvi.SubItems.Add("(Removed)");
                    lvDiff.Items.Add(lvi);
                }
            }
            lvDiff.EndUpdate();
            lblStatus.Text = "Differential comparison complete.";
        }

        private void CopySelected()
        {
            ListView target = tabControl.SelectedIndex == 0 ? lvLive : lvDiff;
            if (target.SelectedItems.Count > 0)
            {
                var item = target.SelectedItems[0];
                string line = string.Join("\t", item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text));
                Clipboard.SetText(line);
                lblStatus.Text = "Copied selected row to clipboard.";
            }
        }

        private void ExportData()
        {
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "Text File (*.txt)|*.txt",
                FileName = "hardware_inventory_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"
            };
            if (sfd.ShowDialog() == DialogResult.OK)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(string.Format("{0,-20} | {1,-15} | {2,-45} | {3,-30}", "CATEGORY", "NAME", "VALUE", "SOURCE"));
                sb.AppendLine(new string('-', 120));
                foreach (var it in currentList)
                {
                    sb.AppendLine(string.Format("{0,-20} | {1,-15} | {2,-45} | {3,-30}", it.Category, it.Name, it.Value, it.Source));
                }
                File.WriteAllText(sfd.FileName, sb.ToString());
                lblStatus.Text = "Exported inventory to " + Path.GetFileName(sfd.FileName);
            }
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
