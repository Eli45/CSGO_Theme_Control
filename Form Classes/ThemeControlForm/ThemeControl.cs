﻿//    This file is part of CSGO Theme Control.
//    Copyright (C) 2015  Elijah Furland      
//
//    CSGO Theme Control is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.
//
//    CSGO Theme Control is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//    GNU General Public License for more details.
//
//    You should have received a copy of the GNU General Public License
//    along with CSGO Theme Control.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using CSGO_Theme_Control.Base_Classes.Constants;
using CSGO_Theme_Control.Base_Classes.Helper;
using CSGO_Theme_Control.Base_Classes.HotKey;
using CSGO_Theme_Control.Base_Classes.Logger;
using CSGO_Theme_Control.Base_Classes.Themes;
using CSGO_Theme_Control.Form_Classes.AdvancedSettingsForm;
using CSGO_Theme_Control.Form_Classes.PickHotKeyForm;
using CSGO_Theme_Control.Form_Classes.RemoveHotKeyForm;

namespace CSGO_Theme_Control.Form_Classes.ThemeControlForm
{
    public partial class ThemeControl : Form
    {
        private readonly bool   DebugMode;
        private bool            IsEnabled               = true;
        private bool            BootOnStart;
        private bool            ShouldChangeDeskTheme;
        private bool            ShouldChangeGameTheme;
        private bool            RegistryBootWritten;
        private bool            ShouldCleanupLogs;

        private string          DesktopThemePath;
        private string          GameThemePath;
        private string          DesktopThemeName;
        private string          GameThemeName;

        private const string    EXE_NAME                = "CSGO_Theme_Control.exe";
        private const string    APP_NAME                = "CSGO_THEME_CONTROL";
        public  const string    VERSION_NUM             = "1.2.0.0";
        public  const string    LOG_DIRECTORY           = "log";

        private Thread               t_IsCSGORunning;
        private readonly RegistryKey rk_StartupKey      = Registry.CurrentUser.OpenSubKey(
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

        //Note(Eli): The value in this dictionary should be the absolute path to the theme to change to.
        private Dictionary<HotKey, ThemePathContainer> HotKeys = new Dictionary<HotKey, ThemePathContainer>();

        [DllImport("user32.dll")]
        private static extern int FindWindow(
            string      lpClassName,
            string      lpWindowName
        );

        [DllImport("user32.dll")]
        private static extern int SendMessage(
            int         hWnd,
            int         Msg,
            int         wParam,
            int         lParam
        );

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(
            IntPtr      hWnd, 
            int         id, 
            int         fsModifiers, 
            int         vk
        );

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(
            IntPtr      hWnd, 
            int         id
        );

        //Used to alt-tab back into the CSGO process after changing themes.
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public ThemeControl()
        {
            InitializeComponent();
            NotificationIcon.Icon = new System.Drawing.Icon(getExeDirectory() + "resources\\Gaben_santa.ico");
            
            //TODO: <- dont remove this.
            //Always set DebugMode to false before release.
            DebugMode = false;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            ReadConfig();

            //Register hotkeys.
            if (HotKeys != null && HotKeys.Count > 0)
                foreach(KeyValuePair<HotKey, ThemePathContainer> entry in HotKeys)
                    RegisterHotKey(Handle, entry.Key.id, entry.Key.keyModifier, entry.Key.keyHashCode);

            //Check appropriate boxes on forms that correlate with user settings.
            chkEnabled.Checked = IsEnabled;
            if (rk_StartupKey.GetValue(APP_NAME) == null) 
                RegistryBootWritten = false;
            else 
                RegistryBootWritten = true;

            BootOnStart            = RegistryBootWritten;
            chkStartOnBoot.Checked = RegistryBootWritten;

            //If CSGO is running when the program is started we should switch themes.
            ShouldChangeDeskTheme  = csgoIsRunning();
            if (ShouldChangeDeskTheme)
            {
                if (GameThemePath != null)
                    changeTheme(GameThemePath);
                else
                    changeTheme(true);
            }
            ShouldChangeGameTheme  = !ShouldChangeDeskTheme;

            //Create new thread to determine if CSGO is ever started.
            t_IsCSGORunning = new Thread(CheckIfRunningForever) { IsBackground = true };
            t_IsCSGORunning.Start();
        }

        private void ThemeControl_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (DebugMode)
            {
                DebugRunTests();
            }

            if (t_IsCSGORunning != null)
            {
                if (t_IsCSGORunning.IsAlive)
                {
                    t_IsCSGORunning.Interrupt();
                    t_IsCSGORunning.Abort();
                }
            }

            WriteConfig();
            if (BootOnStart)
            {
                if (!RegistryBootWritten)
                    createBootStartup();
            }

            if (HotKeys != null && HotKeys.Count > 0)
                foreach (KeyValuePair<HotKey, ThemePathContainer> entry in HotKeys)
                    UnregisterHotKey(Handle, entry.Key.id);

            if (ShouldCleanupLogs)
            {
                FileLogger.CleanLogsFolder();
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
 
            if (m.Msg == Constants.WIN_MSG_HOTKEY_DOWN && IsEnabled)
            {
                //Credit to http://www.fluxbytes.com/csharp/how-to-register-a-global-hotkey-for-your-application-in-c/

                Keys key                        = (Keys)(((int)m.LParam >> 16) & 0xFFFF);
                Constants.KeyModifier modifier  = (Constants.KeyModifier)((int)m.LParam & 0xFFFF);
                int id                          = m.WParam.ToInt32();

                HotKey local = new HotKey(id, (int)modifier, key);
                string pathToTheme = HotKeys[local].GetNextTheme();

                try
                {
                    execCMDThemeChange(pathToTheme);
                }
                catch (Win32Exception e)
                {
                    FileLogger.Log($"File to theme could not be accessed.\nTheme: {pathToTheme}\n" + e.Message, false);
                }
            }
        }

        private void log(string s)
        {
            txtStatus.Text += s + Environment.NewLine;
        }

        private void log(params string[] s)
        {
            foreach (string cur in s)
            {
                txtStatus.Text += cur + Environment.NewLine;
            }
        }

        private void logStatus()
        {
            txtStatus.Text = "";
            string desktopT = (DesktopThemePath == null) ? "Aero" : DesktopThemeName;
            string gameT    = (GameThemePath == null) ? "High Contrast White" : GameThemeName;

            log(
                "Version:"       + HelperFunc.CreateWhiteSpace(7) + VERSION_NUM,
                "Is Enabled:"    + HelperFunc.CreateWhiteSpace(4) + IsEnabled,
                "Boot on start:" + HelperFunc.CreateWhiteSpace(1) + BootOnStart,
                "Cleanup Logs:"  + HelperFunc.CreateWhiteSpace(2) + ShouldCleanupLogs,
                "Desktop theme:" + HelperFunc.CreateWhiteSpace(1) + desktopT,
                "In-game theme:" + HelperFunc.CreateWhiteSpace(1) + gameT
            );

            log("Hotkeys<Key, Theme>:" + HelperFunc.CreateWhiteSpace(4) + "{");
            //TODO: Do I even need to check if the Count is > 0 here?
            if (HotKeys != null && HotKeys.Count > 0)
            {
                foreach (KeyValuePair<HotKey, ThemePathContainer> entry in HotKeys)
                {
                    log(HelperFunc.CreateWhiteSpace(4) + "[" + entry.Key.ToString() + ", " + entry.Value.ToString() + "]");
                }
            }
            log("}");
            
        }

        private void createBootStartup()
        {
            rk_StartupKey.SetValue(APP_NAME, Application.ExecutablePath.ToString());
        }

        private void deleteBootStartup()
        {
            rk_StartupKey.DeleteValue(APP_NAME, false); 
        }

        private void ReadConfig()
        {
            string programExePathFolder = getExeDirectory();

            StreamReader sr = new StreamReader(programExePathFolder + Constants.APP_CONFIG_LOCATION);
            try
            {
                while (!sr.EndOfStream)
                {
                    string line = sr.ReadLine();
                    if (line.StartsWith("//") || line == "" || line == Environment.NewLine)
                        continue;

                    //Note(Eli): This LINQ will only split the line at whitespace OUTSIDE of quotation marks... I think.
                    List<string> split = line.Split('"')
                        .Select((element, index) => index % 2 == 0
                            ? element.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            : new string[] { element })
                        .SelectMany(element => element).ToList();

                    for (int i = 0; i < split.Count; i++)
                    {
                        if (i != split.Count - 1)
                        {
                            if (split[i].StartsWith("\t"))
                            {
                                split[i] = split[i + 1];
                                split.Remove(split[i + 1]);
                            }
                        }
                    }


                    var word = split[0].ToLower();
                    if (word.Equals(nameof(IsEnabled).ToLower()))
                    {
                        try
                        {
                            IsEnabled = Convert.ToBoolean(split[1].ToLower());
                        }
                        catch (IndexOutOfRangeException)
                        {
                            FileLogger.Log("ERROR: CFG is in an invalid format and cannot be read.\nLine: " + line);
                            IsEnabled = false;
                        }
                    }
                    else if (word.Equals(nameof(DesktopThemePath).ToLower()))
                    {
                        try
                        {
                            DesktopThemePath = split[1].ToLower();
                            string[] splitTheme = DesktopThemePath.Split('\\');
                            DesktopThemeName = HelperFunc.UpperCaseFirstChar(splitTheme[splitTheme.Length - 1].Replace(".theme", ""));
                        }
                        catch (IndexOutOfRangeException)
                        {
                            FileLogger.Log("ERROR: CFG is in an invalid format and cannot be read.\nLine: " + line);
                        }
                    }
                    else if (word.Equals(nameof(GameThemePath).ToLower()))
                    {
                        try
                        {
                            GameThemePath = split[1].ToLower();
                            string[] splitTheme = GameThemePath.Split('\\');
                            GameThemeName = HelperFunc.UpperCaseFirstChar(splitTheme[splitTheme.Length - 1].Replace(".theme", ""));
                        }
                        catch (IndexOutOfRangeException)
                        {
                            FileLogger.Log("ERROR: CFG is in an invalid format and cannot be read.\nLine: " + line);
                        }
                    }
                    else if (word.Equals(nameof(ShouldCleanupLogs).ToLower()))
                    {
                        try
                        {
                            ShouldCleanupLogs = Convert.ToBoolean(split[1]);
                        }
                        catch (IndexOutOfRangeException)
                        {
                            FileLogger.Log("ERROR: CFG is in an invalid format and cannot be read.\nLine: " + line);
                        }
                        catch (FormatException)
                        {
                            FileLogger.Log("ERROR: CFG is in an invalid format and cannot be read.\nLine: " + line);
                        }
                    }
                    else if (word.StartsWith("hotkey{"))
                    {
                        try
                        {
                            //using the split line stored in the variable 'split':
                            //index 0 should be "Hotkey{" which is our identifier
                            //index 1 should be the id of the key to assign.
                            //index 2 should be the modifier of the key.
                            //index 3 should be the key itself as a string.
                            //index 4 should be the string path to the theme which will be activated.
                            //index 5 should be the string path to the second theme or null.

                            try 
                            {
                                //Add hotkey to global hotkey list.
                                HotKeys.Add(new HotKey(
                                    Convert.ToInt32(split[1]),
                                    Convert.ToInt32(split[2]),
                                    (Keys)Enum.Parse(typeof(Keys), split[3], false)
                                    ), new ThemePathContainer(split[4], (split[5] == "null") ? "" : split[5]));
                            }
                            catch (Exception e)
                            {
                                if (e is ArgumentNullException || e is ArgumentException || e is OverflowException)
                                {
                                    FileLogger.Log("ERROR: CFG is in an invalid format and cannot be read.\nLine: " + line);
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                        catch (IndexOutOfRangeException)
                        {
                            FileLogger.Log("ERROR: CFG is in an invalid format and cannot be read.\nLine: " + line);
                        }
                    }
                    else
                    {
                        FileLogger.Log("ERROR: CFG is in an invalid format and cannot be read.\nLine: " + line);
                    }
                }
            }
            catch (Exception e)
            {
                if (e is IOException || e is OutOfMemoryException)
                    FileLogger.Log("Could not read CFG file: " + e.Message, false);
                else
                    FileLogger.Log("Unknown exception caught while reading config file." + e.Message, true);
            }
            finally
            {
                sr.Close();
            }

            logStatus();
        }

        private void WriteConfig()
        {
            string programExePathFolder = getExeDirectory();

            StreamWriter sw = new StreamWriter(programExePathFolder + Constants.APP_CONFIG_LOCATION);
            try
            {
                sw.WriteLine("//Note to those reading:\n//Modifying this file could result in the breaking of your config.\n");
                sw.WriteLine(nameof(IsEnabled) + HelperFunc.CreateWhiteSpace(9) + "\"" + IsEnabled.ToString() + "\"");
                sw.WriteLine(nameof(ShouldCleanupLogs) + HelperFunc.CreateWhiteSpace(1) + "\"" + ShouldCleanupLogs.ToString() + "\"");

                if (DesktopThemePath != null)
                {
                    sw.WriteLine(nameof(DesktopThemePath) + HelperFunc.CreateWhiteSpace(5) + "\"" + DesktopThemePath + "\"");
                }
                if (GameThemePath != null)
                {
                    sw.WriteLine(nameof(GameThemePath) + HelperFunc.CreateWhiteSpace(9) + "\"" + GameThemePath + "\"");
                }

                if (HotKeys != null)
                    foreach (KeyValuePair<HotKey, ThemePathContainer> entry in HotKeys)
                    {
                        sw.Write("Hotkey{ ");
                        sw.Write(entry.Key.id + " " + entry.Key.keyModifier + " " + entry.Key.key);
                        sw.Write(" " + entry.Value.ToAbsoluteString());
                        sw.Write(" }\n");
                    }
            }
            catch (Exception e)
            {
                if (e is IOException)
                    FileLogger.Log("Could not read CFG file: " + e.Message, false);
                else
                    FileLogger.Log("Unknown exception caught while reading config file." + e.Message, true);
            }
            finally
            {
                sw.Close();
            }
        }

        public static string getExeDirectory()
        {
            string[] programExePath_split = System.Reflection.Assembly.GetEntryAssembly().Location.Split('\\');
            for (int i = 0; i < programExePath_split.Length; i++)
            {
                if (programExePath_split[i].Equals(EXE_NAME))
                {
                    programExePath_split[i] = "";
                }
            }

            return String.Join("\\", programExePath_split);
        }

        private void CheckIfRunningForever()
        {
            for (;;)
            {
                if (IsEnabled)
                {
                    bool running = csgoIsRunning();
                    if (running)
                    {
                        if (ShouldChangeGameTheme)
                        {
                            if (GameThemePath == null)
                            {
                                changeTheme(true);
                            }
                            else
                            {
                                changeTheme(GameThemePath);
                            }
                            ShouldChangeGameTheme = false;
                            ShouldChangeDeskTheme = true;

                            Thread.Sleep(500); //Wait so we don't alt tab to fast.
                            altTabIntoCSGO();
                        }
                    }
                    else
                    {
                        if (ShouldChangeDeskTheme)
                        {
                            if (DesktopThemePath == null)
                            {
                                changeTheme(false);
                            }
                            else
                            {
                                changeTheme(DesktopThemePath);
                            }
                            ShouldChangeGameTheme = true;
                            ShouldChangeDeskTheme = false;
                        }
                    }          
                }
                try
                {
                    Thread.Sleep(5000);
                }
                catch (ThreadInterruptedException)
                {
                    //Do nothing, only a first chance exception and the program closes immediately after catching this anyway.
                }
            }
        }

        private void chkEnabled_CheckedChanged(object sender, EventArgs e)
        {
            IsEnabled = chkEnabled.Checked;
            if (!IsEnabled)
            {
                if (t_IsCSGORunning != null)
                {
                    t_IsCSGORunning.Abort();
                }
            }
            else
            {
                if (t_IsCSGORunning != null)
                {
                    if (!t_IsCSGORunning.IsAlive)
                    {
                        t_IsCSGORunning = new Thread(CheckIfRunningForever) { IsBackground = true };
                        t_IsCSGORunning.Start();
                    }
                }
            
            }
            logStatus();
        }

        private void chkStartOnBoot_CheckedChanged(object sender, EventArgs e)
        {
            BootOnStart = chkStartOnBoot.Checked;
            if (BootOnStart)
                createBootStartup();
            else
            {
                deleteBootStartup();
            }

            logStatus();
        }

        private static bool csgoIsRunning()
        {
            Process[] Processes = Process.GetProcesses();
            foreach (Process proc in Processes)
            {
                string name = proc.ProcessName;
                
                if (name.Equals(Constants.CSGO_PROC_NAME))
                {
                    return true;
                }
            }

            return false;
        }

        private static void execCMDThemeChange(string PathToFile)
        {
            //Note(Eli): PathToFile should be a full path from the C: directory to the .theme file.
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = String.Format(Constants.CMD_CHANGE_THEME, PathToFile);
            Process process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            Thread.Sleep(500); //Sleep program until the dialog is actually open, so that we can close it.
            int iHandle = FindWindow("CabinetWClass", "Personalization");
            if (iHandle > 0)
            {
                SendMessage(iHandle, Constants.WIN_MSG_WM_SYSCOMMAND, Constants.WIN_MSG_SC_CLOSE, 0);
            }

            //Do a second check to make sure we didn't close it too early in the event of a slow computer etc.
            Thread.Sleep(500);
            iHandle = FindWindow("CabinetWClass", "Personalization");
            if (iHandle > 0)
            {
                SendMessage(iHandle, Constants.WIN_MSG_WM_SYSCOMMAND, Constants.WIN_MSG_SC_CLOSE, 0);
            }
        }

        private void changeTheme(bool useClassic)
        {
            string PATH;
            if (useClassic)
                PATH = Constants.WIN_THEME_CLASSIC;
            else
                PATH = Constants.WIN_THEME_AERO;

            try
            {
                execCMDThemeChange(PATH);
            }
            catch (Win32Exception e)
            {
               FileLogger.Log((String.Format("File to theme could not be accessed.\nTheme: {0}\n", PATH) + e.Message), false);
            }
        }

        //Used for custom themes.
        private void changeTheme(string themePath)
        {
            string PATH = themePath;

            try
            {

                execCMDThemeChange(PATH);
            }
            catch (Win32Exception e)
            {
                FileLogger.Log((String.Format("File to theme could not be accessed.\nTheme: {0}\n", PATH) + e.Message), false);
            }
        }

        private static void altTabIntoCSGO()
        {
            if (Process.GetCurrentProcess().ProcessName.Equals("csgo")) return;

            IntPtr activeWindow = Process.GetCurrentProcess().MainWindowHandle;

            foreach (Process proc in Process.GetProcesses())
            {
                if (activeWindow == proc.MainWindowHandle)
                {
                    if (proc.ProcessName.Equals(Constants.CSGO_PROC_NAME)) return;
                    else
                    {
                        IntPtr csgohWnd = GetCSGOhWnd();
                        if (csgohWnd == IntPtr.Zero)
                            return;

                        SetForegroundWindow(csgohWnd);
                    }
                }
            }
            
        }

        private static IntPtr GetCSGOhWnd()
        {
            //TODO: change from foreach to if or something. foreach doesn't really make sense here but it does work.
            foreach (Process proc in Process.GetProcesses().Where(proc => proc.ProcessName.Equals(Constants.CSGO_PROC_NAME)))
            {
                return proc.MainWindowHandle;
            }

            return IntPtr.Zero;
        }

        private void ThemeControl_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                NotificationIcon.ShowBalloonTip(500);
                Hide();
            }
        }

        private void NotificationIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Show();
                WindowState = FormWindowState.Normal;
            }
            else if (e.Button == MouseButtons.Right)
            {
                contextMenu.Show(MousePosition);
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void GitHubItemToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start("https://github.com/Eli45/CSGO_Theme_Control");
        }

        private void btnChooseDesktop_Click(object sender, EventArgs e)
        {
            DialogResult result = openFileDialog.ShowDialog();
            if (result == DialogResult.OK)  //User selected a file and clicked ok
            {
                string filepath = openFileDialog.FileName;
                DesktopThemePath = filepath;
                string[] split = filepath.Split('\\');
                DesktopThemeName = HelperFunc.UpperCaseFirstChar(split[split.Length - 1].Replace(".theme", ""));
            }
            logStatus();
        }

        private void btnChooseIngame_Click(object sender, EventArgs e)
        {
            DialogResult result = openFileDialog.ShowDialog();
            if (result == DialogResult.OK)  //User selected a file and clicked ok
            {
                string filepath = openFileDialog.FileName;
                GameThemePath = filepath;
                string[] split = filepath.Split('\\');
                GameThemeName = HelperFunc.UpperCaseFirstChar(split[split.Length - 1].Replace(".theme", ""));
            }
            logStatus();
        }

        private void btnClearThemes_Click(object sender, EventArgs e)
        {
            GameThemePath      = null;
            DesktopThemePath   = null;
            logStatus();
        }

        private void btnPickHotkeys_Click(object sender, EventArgs e)
        {
            unsafe
            {
                HotKeyDataHolder hkdh;
                ThemeDataHolder tdh;
                HotKeyPickerForm hkpf = new HotKeyPickerForm(&hkdh, &tdh, HotKeys);
                Form casted = hkpf;
                HelperFunc.CreateFormStartPosition(ref casted, this);
                hkpf = (HotKeyPickerForm)casted;

                DialogResult result = hkpf.ShowDialog();

                if (result == DialogResult.OK)
                {
                    string themePathFromCSTR1 = new string(tdh.ThemePath1);
                    string themePathFromCSTR2 = new string(tdh.ThemePath2);

                    //After the form is closed we can make a new KeyValuePair for our dictionary and register the key.
                    RegisterHotKey(Handle, hkdh.id, hkdh.keyModifier, hkdh.keyHashCode);
                    HotKeys.Add(
                        HotKey.FormNewHotKey(hkdh),
                        new ThemePathContainer(themePathFromCSTR1, themePathFromCSTR2)
                    );
                }
            }

            logStatus();
        }

        private void btnRemoveHotkey_Click(object sender, EventArgs e)
        {
            unsafe
            {
                HotKeyDataHolder hkdh;
                HotKeyRemovalForm hkrf = new HotKeyRemovalForm(&hkdh, HotKeys);
                Form casted = hkrf;
                HelperFunc.CreateFormStartPosition(ref casted, this);
                hkrf = (HotKeyRemovalForm)casted;

                DialogResult result = hkrf.ShowDialog();
                switch (result)
                {
                    case DialogResult.OK:
                        HotKey hk = new HotKey(hkdh.id, hkdh.keyModifier, hkdh.key);
                        HotKeys.Remove(hk);
                        UnregisterHotKey(Handle, hk.id);
                        break;

                    case DialogResult.Yes:
                        HotKeys = new Dictionary<HotKey, ThemePathContainer>();
                        break;
                }
            }

            logStatus();
        }

        private void btnOpenAdvanced_Click(object sender, EventArgs e)
        {
            AdvancedUserSettingsForm f = new AdvancedUserSettingsForm(ShouldCleanupLogs);
            DialogResult result = f.ShowDialog();

            if (result == DialogResult.OK)
            {
                ShouldCleanupLogs = f.CleanLogs;
                //TODO: Add more.
            }
        }

        private static void DebugRunTests()
        {
            FileLogger.Log("This is just a test crash dump. You should not be seeing this!", false);
            FileLogger.Log("This is just a test crash dump. You should not be seeing this! This was thrown!", true);
            //Note(Eli): There really should be some other stuff here but I just don't know what else we could test due to the nature of the program.
        }
    }
}
