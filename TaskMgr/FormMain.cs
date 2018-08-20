﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using PCMgr.Aero.TaskDialog;
using PCMgr.Ctls;
using PCMgr.Helpers;
using PCMgr.Lanuages;
using PCMgr.WorkWindow;
using PCMgrUWP;
using static PCMgr.NativeMethods;
using static PCMgr.NativeMethods.Win32;
using static PCMgr.NativeMethods.DeviceApi;

namespace PCMgr
{
    public partial class FormMain : Form
    {
        public FormMain(string[] agrs)
        {
            Instance = this;
            InitializeComponent();

            baseProcessRefeshTimer.Interval = 1000;
            baseProcessRefeshTimer.Tick += BaseProcessRefeshTimer_Tick;
            listProcess.Header.CloumClick += Header_CloumClick;
            baseProcessRefeshTimerLow.Interval = 10000;
            baseProcessRefeshTimerLow.Tick += BaseProcessRefeshTimerLow_Tick;
            baseProcessRefeshTimerLowSc.Interval = 120000;
            baseProcessRefeshTimerLowSc.Tick += BaseProcessRefeshTimerLowSc_Tick;
            this.agrs = agrs;
        }

        public static string cfgFilePath = "";
        private string[] agrs = null;

        //private bool showSystemProcess = false;
        private bool showHiddenFiles = false;

        private bool processListInited = false;
        private bool driverListInited = false;
        private bool scListInited = false;
        private bool fileListInited = false;
        private bool startListInited = false;
        private bool uwpListInited = false;
        private bool perfInited = false;
        private bool perfMainInited = false;
        private bool perfMainInitFailed = false;

        public static FormMain Instance { private set; get; }
        public const string MICROSOFT = "Microsoft Corporation";

        #region ProcessListWork

        private const double PERF_LIMIT_MIN_DATA_DISK = 0.01;
        private const double PERF_LIMIT_MIN_DATA_NETWORK = PERF_LIMIT_MIN_DATA_DISK;

        private bool refeshLowLock = false;
        private Size lastSimpleSize = new Size();
        private Size lastSize = new Size();
        private int nextSecType = -1;
        private int sortitem = -1;
        private bool sorta = false;
        private bool isFirstLoad = true;
        private bool mergeApps = true;
        private Timer baseProcessRefeshTimer = new Timer();
        private Timer baseProcessRefeshTimerLow = new Timer();
        private Timer baseProcessRefeshTimerLowSc = new Timer();
        private TaskListViewColumnSorter lvwColumnSorter = null;

        private class PsItem
        {
            public IntPtr SYSTEM_PROCESSES = IntPtr.Zero;
            public IntPtr perfData;
            public IntPtr handle;
            public uint pid;
            public uint ppid;
            public string exename;
            public string exepath;
            public TaskMgrListItem item = null;
            public bool isSvchost = false;
            public bool isUWP = false;
            public bool isWindowShow = false;
            public bool isWindowsProcess = false;

            public uwpitem uwpItem = null;
            public string uwpFullName;

            public bool updateLock = false;

            public PsItem parent = null;
            public List<PsItem> childs = new List<PsItem>();
            public List<ScItem> svcs = new List<ScItem>();
        }
        private class uwpitem
        {
            public string uwpInstallDir = "";
            public TaskMgrListItemGroup uwpItem = null;
            public string uwpFullName = "";
        }
        private class uwpwinitem
        {
            public IntPtr hWnd = IntPtr.Zero;
            public string title = "";
        }
        private class uwphostitem
        {
            public uwphostitem(uwpitem item, uint pid)
            {
                this.pid = pid;
                this.item = item;
            }

            public uwpitem item;
            public uint pid;
        }

        private bool isSimpleView
        {
            get { return _isSimpleView; }
            set
            {
                _isSimpleView = value;
                if (_isSimpleView)
                {
                    MAppWorkCall3(215, Handle, IntPtr.Zero);
                    pl_simple.Show();
                    tabControlMain.Hide();
                    baseProcessRefeshTimer.Interval = 2000;
                    baseProcessRefeshTimer.Start();
                    BaseProcessRefeshTimer_Tick(this, null);
                    baseProcessRefeshTimerLow.Start();
                    listProcess.Locked = true;
                }
                else
                {
                    MAppWorkCall3(216, Handle, IntPtr.Zero);
                    listApps.Items.Clear();
                    pl_simple.Hide();
                    tabControlMain.Show();
                    LoadRefeshRateSetting();
                    listProcess.Locked = false;
                    ProcessListForceRefeshAll();
                }
            }
        }


        private bool _isSimpleView = false;
        private bool is64OS = false;
        private bool isSelectExplorer = false;
        private uint currentProcessPid = 0;
        private List<uint> validPid = new List<uint>();
        private List<uwphostitem> uwpHostPid = new List<uwphostitem>();
        private List<PsItem> loadedPs = new List<PsItem>();
        private List<uwpitem> uwps = new List<uwpitem>();
        private List<uwpwinitem> uwpwins = new List<uwpwinitem>();
        private List<string> windowsProcess = new List<string>();
        private List<string> veryimporantProcess = new List<string>();
        private Color dataGridZeroColor = Color.FromArgb(255, 244, 196);

        private TaskMgrListItem nextKillItem = null;
        private bool isRunAsAdmin = false;
        private bool firstLoad = true;
        private Font smallListFont = new Font("微软雅黑", 9f);
        private TaskMgrListItem thisLoadItem = null;

        private void MainGetWinsCallBack(IntPtr hWnd, IntPtr data, int i)
        {
            if (i == 1)
            {
                if (IsWindowVisible(hWnd))
                {
                    uwpwinitem item = new uwpwinitem();
                    item.hWnd = hWnd;
                    item.title = Marshal.PtrToStringAuto(data);
                    uwpwins.Add(item);
                }
            }
            else
            {
                if (thisLoadItem != null)
                {
                    if (((PsItem)thisLoadItem.Tag).exepath.ToLower() != @"c:\windows\system32\dwm.exe")
                    {
                        if (!thisLoadItem.HasWindowChild(hWnd))
                        {
                            if (IsWindowVisible(hWnd))
                            {
                                IntPtr icon = MGetWindowIcon(hWnd);
                                TaskMgrListItemChild c = new TaskMgrListItemChild(Marshal.PtrToStringAuto(data), icon != IntPtr.Zero ? Icon.FromHandle(icon) : PCMgr.Properties.Resources.icoShowedWindow);
                                c.Tag = hWnd;
                                c.Type = TaskMgrListItemType.ItemWindow;
                                thisLoadItem.Childs.Add(c);
                            }
                        }
                    }
                }
            }
        }
        private void MainEnumWinsCallBack(IntPtr hWnd, IntPtr hWndParent)
        {
            WorkWindow.FormSpyWindow f = new WorkWindow.FormSpyWindow(hWnd);
            Control fp = FromHandle(hWndParent);
            f.ShowDialog(fp);
        }

        private bool IsVeryImporant(PsItem p)
        {
            if (p.exepath != null)
            {
                string str = p.exepath.ToLower();
                foreach (string s in veryimporantProcess)
                    if (s == str) return true;
            }
            return false;
        }
        private bool IsImporant(PsItem p)
        {
            /*if (p.exepath != null)
            {
                if (p.exepath.ToLower() == @"c:\windows\system32\svchost.exe") return true;
                if (p.exepath.ToLower() == @"c:\windows\system32\cssrs.exe") return true;
                if (p.exepath.ToLower() == @"c:\windows\system32\smss.exe") return true;
                if (p.exepath.ToLower() == @"c:\windows\system32\lsass.exe") return true;
                if (p.exepath.ToLower() == @"c:\windows\system32\sihost.exe") return true;
                if (p.exepath.ToLower() == @"c:\windows\system32\cssrs.exe") return true;
               
            }*/
            if (p.exepath != null)
            {
                if (p.exepath.ToLower() == @"c:\windows\system32\svchost.exe") return true;
                return IsWindowsProcess(p.exepath);
            }
            return false;
        }
        private bool IsExplorer(PsItem p)
        {
            if (p.exename != null && p.exename.ToLower() == "explorer.exe") return true;
            if (p.exepath != null && p.exepath.ToLower() == @"c:\windows\explorer.exe") return true;
            return false;
        }
        private bool IsWindowsProcess(string str)
        {
            //检测是不是Windows进程
            if (str != null)
            {
                str = str.ToLower();
                foreach (string s in windowsProcess)
                    if (s == str) return true;
            }
            return false;
        }

        private bool ProcessListGetUwpIsRunning(string dsbText)
        {
            bool rs = false;
            foreach (uwpwinitem u in uwpwins)
                if (u.title.Contains(dsbText))
                {
                    rs = true;
                    break;
                }
            return rs;
        }
        private Color ProcessListGetColorFormValue(double v, double maxv)
        {
            //数值百分百转为颜色
            double d = v / maxv;
            if (d <= 0)
                return Color.FromArgb(255, 244, 196);
            else if (d > 0 && d <= 0.1)
                return Color.FromArgb(249, 236, 168);
            else if (d > 0.1 && d <= 0.3)
                return Color.FromArgb(255, 228, 135);
            else if (d > 0.3 && d <= 0.6)
                return Color.FromArgb(252, 207, 23);
            else if (d > 0.6 && d <= 0.8)
                return Color.FromArgb(252, 184, 22);
            else if (d > 0.8 && d <= 0.9)
                return Color.FromArgb(255, 167, 29);
            else if (d > 0.9)
                return Color.FromArgb(255, 160, 19);
            return Color.FromArgb(255, 249, 228);
        }

        //查找条目
        private uwphostitem ProcessListFindUWPItemWithHostId(uint pid)
        {
            uwphostitem rs = null;
            foreach (uwphostitem i in uwpHostPid)
            {
                if (i.pid == pid)
                {
                    rs = i;
                    break;
                }
            }
            return rs;
        }
        private uwpitem ProcessListFindUWPItem(string fullName)
        {
            uwpitem rs = null;
            foreach (uwpitem i in uwps)
            {
                if (i.uwpFullName == fullName)
                {
                    rs = i;
                    break;
                }
            }
            return rs;
        }
        private PsItem ProcessListFindPsItem(uint pid)
        {
            PsItem rs = null;
            foreach (PsItem i in loadedPs)
            {
                if (i.pid == pid)
                {
                    rs = i;
                    return rs;
                }
            }
            return rs;
        }
        private TaskMgrListItem ProcessListFindItem(uint pid)
        {
            TaskMgrListItem rs = null;
            foreach (TaskMgrListItem i in listProcess.Items)
            {
                if (i.PID == pid)
                {
                    rs = i;
                    return rs;
                }
                if (i.Type == TaskMgrListItemType.ItemProcessHost
                    || i.Type == TaskMgrListItemType.ItemUWPProcess)
                {
                    foreach (TaskMgrListItem ix in i.Childs)
                    {
                        if (ix.PID == pid)
                        {
                            rs = ix;
                            return rs;
                        }
                    }
                }
            }
            return rs;
        }
        private bool ProcessListIsProcessLoaded(uint pid, out PsItem item)
        {
            bool rs = false;
            foreach (PsItem f in loadedPs)
            {
                if (f.pid == pid)
                {
                    item = f;
                    rs = true;
                    return rs;
                }
            }
            item = null;
            return rs;
        }

        private void ProcessListInitIn1Slater()
        {
            if (!processListInited)
            {
                Timer t = new Timer();
                t.Interval = 50;
                t.Tick += ProcessListInitIn1T_Tick;
                t.Start();//缓冲一下防止界面还未显示时卡顿
            }
            else
            {
                if (!listProcess.Visible) listProcess.Show();
                StartingProgressShowHide(false);
            }
        }
        private void ProcessListInitIn1T_Tick(object sender, EventArgs e)
        {
            (sender as Timer).Stop();
            ProcessListInit();
        }

        private void ProcessListInitLater()
        {
            if (!perfMainInited && !perfMainInitFailed)
                ProcessListInitPerfs();
        }
        private void ProcessListInitPerfs()
        {
            if (!perfMainInitFailed)
            {
                //初始化整体性能计数器
                MPERF_Init3PerformanceCounters();
                ProcessListForceRefeshAll();
                perfMainInited = true; 
            }
        }
        private void ProcessListUnInitPerfs()
        {
            if (perfMainInited)
            {
                //释放计数器
                MPERF_Destroy3PerformanceCounters();
            }
        }
        private void ProcessListInit()
        {
            //初始化
            if (!processListInited)
            {
                currentProcessPid = (uint)MAppWorkCall3(180, IntPtr.Zero, IntPtr.Zero);

                enumProcessCallBack = ProcessListHandle;
                enumProcessCallBack2 = ProcessListHandle2;

                enumProcessCallBack_ptr = Marshal.GetFunctionPointerForDelegate(enumProcessCallBack);
                enumProcessCallBack2_ptr = Marshal.GetFunctionPointerForDelegate(enumProcessCallBack2);

                baseProcessRefeshTimer.Start();
                baseProcessRefeshTimerLow.Start();
                baseProcessRefeshTimerLowSc.Start();
                isRunAsAdmin = MIsRunasAdmin();

                if (!isRunAsAdmin)
                {
                    spl1.Visible = true;
                    check_showAllProcess.Visible = true;
                }

                windowsProcess.Add(@"C:\Program Files\Windows Defender\NisSrv.exe".ToLower());
                windowsProcess.Add(@"C:\Program Files\Windows Defender\MsMpEng.exe".ToLower());
                windowsProcess.Add(@"C:\Windows\System32\svchost.exe".ToLower());
                windowsProcess.Add(@"C:\Windows\System32\csrss.exe".ToLower());
                windowsProcess.Add(@"C:\Windows\System32\conhost.exe".ToLower());
                windowsProcess.Add(@"‪C:\Windows\System32\sihost.exe".ToLower());
                windowsProcess.Add(@"C:\Windows\System32\winlogon.exe".ToLower());
                windowsProcess.Add(@"C:\Windows\System32\wininit.exe".ToLower());
                windowsProcess.Add(@"C:\Windows\System32\smss.exe".ToLower());
                windowsProcess.Add(@"C:\Windows\System32\services.exe".ToLower());
                windowsProcess.Add(@"C:\Windows\System32\dwm.exe".ToLower());
                windowsProcess.Add("c:\\windows\\system32\\lsass.exe");
                windowsProcess.Add("c:\\windows\\explorer.exe");

                veryimporantProcess.Add(@"C:\Windows\System32\wininit.exe".ToLower());
                veryimporantProcess.Add("c:\\windows\\system32\\csrss.exe".ToLower());
                veryimporantProcess.Add("c:\\windows\\system32\\lsass.exe");
                veryimporantProcess.Add("c:\\windows\\system32\\smss.exe");
                //veryimporantProcess.Add(@"‪".ToLower());
                //veryimporantProcess.Add(@"‪".ToLower());

                /*
                windowsProcess.Add(@"".ToLower());
                windowsProcess.Add(@"".ToLower());       
                windowsProcess.Add(@"".ToLower());        
                windowsProcess.Add(@"".ToLower());   
                windowsProcess.Add(@"".ToLower());       
                windowsProcess.Add(@"".ToLower());      
                windowsProcess.Add(@"".ToLower());          
                windowsProcess.Add(@"".ToLower());         
                windowsProcess.Add(@"".ToLower());      
                windowsProcess.Add(@"".ToLower());      
                windowsProcess.Add(@"".ToLower());
                windowsProcess.Add(@"".ToLower());
                windowsProcess.Add(@"".ToLower());
                windowsProcess.Add(@"".ToLower());
                windowsProcess.Add(@"".ToLower());
                */

                processListInited = true;

                if (MIsRunasAdmin())
                    ScMgrInit();
                if (SysVer.IsWin8Upper())
                    UWPListInit();

                ProcessListRefesh();
                ProcessListSimpleInit();

                StartingProgressShowHide(false);
            }
        }

        private void ProcessListRefesh()
        {
            //清空整个列表并加载

            uwps.Clear();
            uwpHostPid.Clear();
            uwpwins.Clear();

            if (SysVer.IsWin8Upper()) MAppVProcessAllWindowsUWP();

            ProcessListPrepareClear();
            listProcess.Locked = true;
            MEnumProcess(enumProcessCallBack_ptr);
        }
        private void ProcessListRefesh1Finished()
        {
            //加载结束
            if (firstLoad) ProcessListLoadFinished();

            ProcessListClear();
            ProcessListRefeshPidTree();
            lbProcessCount.Text = str_proc_count + " : " + listProcess.Items.Count;
            if (isFirstLoad)
            {
                if (sortitem < listProcess.Header.Items.Count && sortitem >= 0)
                {
                    lvwColumnSorter.Order = sorta ? SortOrder.Ascending : SortOrder.Descending;
                    lvwColumnSorter.SortColumn = sortitem;
                    listProcess.Header.Items[sortitem].ArrowType = sorta ? TaskMgrListHeaderSortArrow.Ascending : TaskMgrListHeaderSortArrow.Descending;
                    listProcess.Header.Invalidate();
                    listProcess.ListViewItemSorter = lvwColumnSorter;
                    if (sortitem == 0) listProcess.ShowGroup = true;
                    else listProcess.ShowGroup = false;
                    listProcess.SyncItems(false);
                    listProcess.Sort();
                }
                isFirstLoad = false;
            }
            refeshLowLock = true;
            ProcessListForceRefeshAll();
            refeshLowLock = false;
            listProcess.Locked = false;
            listProcess.SyncItems(true);
        }
        private void ProcessListRefesh2()
        {
            if (cpuindex != -1) MPERF_CpuTimeUpdate();
            if (netindex != -1) MPERF_NET_UpdateAllProcessNetInfo();
            uwpwins.Clear();

            //刷新所有数据
            ProcessListPrepareClear();
            listProcess.Locked = true;
            MEnumProcess2Refesh(enumProcessCallBack2_ptr);
            ProcessListClear();
       
            //枚举一些UWP应用
            if (SysVer.IsWin8Upper()) MAppVProcessAllWindowsUWP();

            //刷新性能数据
            bool refeshAColumData = lvwColumnSorter.SortColumn == cpuindex
                || lvwColumnSorter.SortColumn == ramindex
                || lvwColumnSorter.SortColumn == diskindex
                || lvwColumnSorter.SortColumn == netindex
                || lvwColumnSorter.SortColumn == stateindex;
            ProcessListUpdateValues(refeshAColumData ? lvwColumnSorter.SortColumn : -1);
            ProcessListRefeshPidTree();

            if (!isSimpleView)
            {
                if (refeshAColumData)
                    listProcess.Sort(false);//排序
                listProcess.Locked = false;
                //刷新列表
                listProcess.SyncItems(true);

                lbProcessCount.Text = str_proc_count + " : " + listProcess.Items.Count;
            }
            else
            {
                listProcess.Locked = false;
                ProcessListSimpleRefesh();
            }

            ProcessListKillLastEndItem();
        }
        private void ProcessListRefeshPidTree()
        {
            //Refesh Pid tree
            foreach (PsItem p in loadedPs)
                p.childs.Clear();
            foreach (PsItem p in loadedPs)
            {
                PsItem parent = ProcessListFindPsItem(p.ppid);
                if (parent != null)
                    parent.childs.Add(p);
                else if (p.parent != null)
                {
                    if (p.parent.childs.Contains(p))
                        p.parent.childs.Remove(p);
                }
                p.parent = p;
            }
        }
        private void ProcessListForceRefeshAll()
        {
            for (int i = 0; i < listProcess.Items.Count; i++)
            {
                //强制刷新所有的条目
                if (listProcess.Items[i].Type == TaskMgrListItemType.ItemUWPHost)
                    ProcessListUpdate(listProcess.Items[i].PID, false, listProcess.Items[i], IntPtr.Zero, -1);
                else ProcessListUpdate(listProcess.Items[i].PID, false, listProcess.Items[i], ((PsItem)listProcess.Items[i].Tag).SYSTEM_PROCESSES, -1);
            }
        }

        private void ProcessListLoad(uint pid, uint ppid, string exename, string exefullpath, IntPtr hprocess, IntPtr system_process)
        {
            bool need_add_tolist = true;
            //base
            PsItem p = new PsItem();
            p.SYSTEM_PROCESSES = system_process;
            p.pid = pid;
            p.ppid = ppid;
            loadedPs.Add(p);

            PsItem parentpsItem = null;
            if (ProcessListIsProcessLoaded(p.ppid, out parentpsItem))
            {
                p.parent = parentpsItem;
                parentpsItem.childs.Add(p);
            }

            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append(exefullpath);

            bool epgeted = false;
            PEOCESSKINFO infoStruct = new PEOCESSKINFO();
            if (canUseKernel)
            {
                if (MGetProcessEprocess(pid, ref infoStruct))
                {
                    epgeted = true;
                    if (string.IsNullOrEmpty(exefullpath))
                    {
                        exefullpath = infoStruct.ImageFullName;
                        stringBuilder.Append(exefullpath);
                    }
                }
            }

            TaskMgrListItem taskMgrListItem;
            if (pid == 0) taskMgrListItem = new TaskMgrListItem(str_idle_process);
            else if (pid == 4) taskMgrListItem = new TaskMgrListItem("System");
            else if (pid == 88 && exename == "Registry") taskMgrListItem = new TaskMgrListItem("Registry");
            else if (exename == "Memory Compression") taskMgrListItem = new TaskMgrListItem("Memory Compression");
            else if (stringBuilder.ToString() != "")
            {
                StringBuilder exeDescribe = new StringBuilder(256);

                if (MGetExeDescribe(stringBuilder.ToString(), exeDescribe, 256))
                {
                    string exeDescribeStr = exeDescribe.ToString();
                    exeDescribeStr = exeDescribeStr.Trim();
                    if (exeDescribeStr != "")
                        taskMgrListItem = new TaskMgrListItem(exeDescribeStr);
                    else taskMgrListItem = new TaskMgrListItem(exename);
                }
                else taskMgrListItem = new TaskMgrListItem(exename);
            }
            else taskMgrListItem = new TaskMgrListItem(exename);
            //test is 32 bit app in 64os
            if (is64OS)
            {
                if (hprocess != IntPtr.Zero)
                {
                    if (MGetProcessIs32Bit(hprocess))
                        taskMgrListItem.Text = taskMgrListItem.Text + " (" + str_proc_32 + ")";
                }
            }

            p.item = taskMgrListItem;
            p.perfData = MPERF_PerfDataCreate();
            p.handle = hprocess;
            p.exename = exename;
            p.pid = pid;
            p.exepath = stringBuilder.ToString();
            p.isWindowsProcess = (pid == 0 || pid == 4
                            || (pid == 88 && exename == "Registry")
                            || (pid < 1024 && exename == "csrss.exe")
                            || exename == "Memory Compression"
                            || IsWindowsProcess(exefullpath));
            taskMgrListItem.Type = TaskMgrListItemType.ItemProcess;
            taskMgrListItem.IsFullData = true;

            //Test service
            bool isSvcHoct = false;
            if (exefullpath != null && (exefullpath.ToLower() == @"c:\windows\system32\svchost.exe" || exefullpath.ToLower() == @"c:\windows\syswow64\svchost.exe") || exename == "svchost.exe")
            {
                //svchost.exe add a icon
                taskMgrListItem.Icon = PCMgr.Properties.Resources.icoServiceHost;
                isSvcHoct = true;
            }
            else
            {
                //get pe icon
                IntPtr intPtr = MGetExeIcon(stringBuilder.ToString());
                if (intPtr != IntPtr.Zero) taskMgrListItem.Icon = Icon.FromHandle(intPtr);
            }
            //try get service info
            if (scCanUse && scValidPid.Contains(pid))
            {
                //find sc item
                if (ScMgrFindRunSc(p))
                {
                    if (isSvcHoct)
                    {
                        if (p.svcs.Count == 1)
                        {
                            if (!string.IsNullOrEmpty(p.svcs[0].groupName))
                                taskMgrListItem.Text = str_service_host + " : " + p.svcs[0].scName + " (" + ScGroupNameToFriendlyName(p.svcs[0].groupName) + ")";
                            else taskMgrListItem.Text = str_service_host + " : " + p.svcs[0].scName;
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(p.svcs[0].groupName))
                                taskMgrListItem.Text = str_service_host + " : " + ScGroupNameToFriendlyName(p.svcs[0].groupName) + "(" + p.svcs.Count + ")";
                            else taskMgrListItem.Text = str_service_host + " (" + p.svcs.Count + ")";
                        }
                    }
                    TaskMgrListItemChild tx = null;
                    for (int i = 0; i < p.svcs.Count; i++)
                    {
                        tx = new TaskMgrListItemChild(p.svcs[0].scDsb, icoSc);
                        tx.Tag = p.svcs[0].scName;
                        tx.Type = TaskMgrListItemType.ItemService;
                        taskMgrListItem.Childs.Add(tx);
                    }
                    p.isSvchost = true;
                }
            }

           // if (pid == 6064)
            //    ;
            //if ((exefullpath != null && exefullpath.ToLower() == @"‪c:\windows\explorer.exe") 
            //    || (exename != null && exename.ToLower() == @"‪explorer.exe"))
             //   explorerPid = pid;

            //ps data item
            if (SysVer.IsWin8Upper())
                p.isUWP = hprocess == IntPtr.Zero ? false : MGetProcessIsUWP(hprocess);

            taskMgrListItem.Tag = p;

            //10 empty item
            for (int i = 0; i < 10; i++) taskMgrListItem.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());

            //UWP app

            uwphostitem hostitem = null;
            if (p.isUWP)
            {
                taskMgrListItem.IsUWP = true;
                if (stateindex != -1)
                {
                    taskMgrListItem.DrawUWPPausedGray = true;
                    taskMgrListItem.SubItems[stateindex].DrawUWPPausedIcon = true;
                }
                if (uwpListInited)
                {
                    //get fullname
                    int len = 0;
                    if (!MGetUWPPackageFullName(hprocess, ref len, null))
                        goto OUT;
                    StringBuilder b = new StringBuilder(len);
                    if (!MGetUWPPackageFullName(hprocess, ref len, b))
                        goto OUT;
                    p.uwpFullName = b.ToString();
                    if (p.uwpFullName == "")
                        goto OUT;
                    TaskMgrListItem uapp = UWPListFindItem(p.uwpFullName);
                    if (uapp == null)
                        goto OUT;
                    //copy data form uwp app list

                    taskMgrListItem.Text = uapp.Text;
                    taskMgrListItem.Icon = uapp.Icon;
                    if (companyindex != -1)
                        taskMgrListItem.SubItems[companyindex].Text = taskMgrListItem.SubItems[1].Text;
                    taskMgrListItem.IsUWPICO = true;
                    taskMgrListItem.IsFullData = true;
                    taskMgrListItem.Type = TaskMgrListItemType.ItemUWPProcess;
                    taskMgrListItem.IsChildItem = true;

                    uwpitem parentItem = ProcessListFindUWPItem(p.uwpFullName);
                    if (parentItem != null)
                    {
                        //Fill this item to parent item
                        TaskMgrListItemGroup g = parentItem.uwpItem;
                        g.Icon = uapp.Icon;
                        g.Image = uapp.Image;
                        g.Type = TaskMgrListItemType.ItemUWPHost;
                        g.Childs.Add(taskMgrListItem);
                        g.Text = uapp.Text;
                        g.DisplayChildCount = g.Childs.Count > 1;
                        p.uwpItem = parentItem;

                        if (ProcessListFindUWPItemWithHostId(p.pid) == null) uwpHostPid.Add(new uwphostitem(parentItem, p.pid));

                        need_add_tolist = false;
                    }
                    else
                    {
                        //create new uwp item and add this to parent item
                        parentItem = new uwpitem();

                        TaskMgrListItemGroup g = new TaskMgrListItemGroup(uapp.Text);
                        g.Icon = uapp.Icon;
                        g.Image = uapp.Image;
                        g.Childs.Add(taskMgrListItem);
                        g.Type = TaskMgrListItemType.ItemUWPHost;
                        g.Group = listProcess.Groups[1];
                        g.IsUWPICO = true;

                        g.PID = (uint)1;
                        //10 empty item
                        for (int i = 0; i < 10; i++) g.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem() { Font = listProcess.Font });
                        if (stateindex != -1)
                        {
                            g.DrawUWPPausedIconIndex = stateindex;
                            g.SubItems[stateindex].DrawUWPPausedIcon = true;
                        }
                        if (nameindex != -1) g.SubItems[nameindex].Text = p.uwpFullName;
                        if (pathindex != -1) g.SubItems[pathindex].Text = uapp.SubItems[4].Text;

                        g.Tag = parentItem;

                        parentItem.uwpInstallDir = uapp.SubItems[4].Text;
                        parentItem.uwpFullName = p.uwpFullName;
                        parentItem.uwpItem = g;
                        p.uwpItem = parentItem;

                        if (ProcessListFindUWPItemWithHostId(p.pid) == null) uwpHostPid.Add(new uwphostitem(parentItem, p.pid));

                        uwps.Add(parentItem);
                        listProcess.Items.Add(g);
                        need_add_tolist = false;
                    }
                }
            }
            OUT:
            if (need_add_tolist)
            {
                hostitem = ProcessListFindUWPItemWithHostId(ppid);
                //UWP app childs
                if (hostitem != null)
                {
                    hostitem.item.uwpItem.Childs.Add(taskMgrListItem);
                    need_add_tolist = false;
                }
            }

            //data items

            if (stateindex != -1) taskMgrListItem.DrawUWPPausedIconIndex = stateindex;
            if (nameindex != -1)
            {
                if (pid == 0) taskMgrListItem.SubItems[nameindex].Text = str_idle_process;
                else if (pid == 4) taskMgrListItem.SubItems[nameindex].Text = "ntoskrnl.exe";
                else taskMgrListItem.SubItems[nameindex].Text = exename;
            }
            if (pidindex != -1)
            {
                taskMgrListItem.SubItems[pidindex].Text = pid.ToString();
            }
            if (pathindex != -1) if (stringBuilder.ToString() != "") taskMgrListItem.SubItems[pathindex].Text = stringBuilder.ToString();
            if (cmdindex != -1)
            {
                StringBuilder s = new StringBuilder(1024);
                if (MGetProcessCommandLine(hprocess, s, 1024, pid))
                    taskMgrListItem.SubItems[cmdindex].Text = s.ToString();
            }
            if (companyindex != -1)
            {
                if (stringBuilder.ToString() != "")
                {
                    StringBuilder exeCompany = new StringBuilder(256);
                    if (MGetExeCompany(stringBuilder.ToString(), exeCompany, 256)) taskMgrListItem.SubItems[companyindex].Text = exeCompany.ToString();
                }
            }
            if (eprocessindex != -1)
            {
                if (epgeted) taskMgrListItem.SubItems[eprocessindex].Text = infoStruct.Eprocess;
                else taskMgrListItem.SubItems[eprocessindex].Text = "-";
            }

            //Init performance

            for (int i = 1; i < taskMgrListItem.SubItems.Count; i++)
                taskMgrListItem.SubItems[i].Font = smallListFont;

            thisLoadItem = taskMgrListItem;
            MAppVProcessAllWindowsGetProcessWindow(pid);
            thisLoadItem = null;

            if (taskMgrListItem.Childs.Count > 0)
                taskMgrListItem.Group = listProcess.Groups[0];
            else if (pid == 0 || pid == 4 || IsWindowsProcess(exefullpath))
                taskMgrListItem.Group = listProcess.Groups[2];
            else taskMgrListItem.Group = listProcess.Groups[1];

            taskMgrListItem.PID = pid;
            if (need_add_tolist) listProcess.Items.Add(taskMgrListItem);
            ProcessListUpdate(pid, true, taskMgrListItem, system_process);
        }
        private void ProcessListUpdate(uint pid, bool isload, TaskMgrListItem it, IntPtr system_process, int ipdateOneDataCloum = -1, bool forceProcessHost = false)
        {
            if (!forceProcessHost && (it.Type == TaskMgrListItemType.ItemUWPHost || it.Type == TaskMgrListItemType.ItemProcessHost))
            {
                //Group uppdate
                ProcessListUpdate_GroupChilds(isload, it, ipdateOneDataCloum);

                if (it.Type == TaskMgrListItemType.ItemUWPHost)
                {
                    bool running = false;
                    bool ispause = false;
                    if (stateindex != -1 && ipdateOneDataCloum != stateindex && it.Childs.Count > 0)
                    {
                        foreach (TaskMgrListItem ix in it.Childs)
                            if (ix.Type == TaskMgrListItemType.ItemProcess)
                            {
                                if (ix.SubItems[stateindex].Text == str_status_paused)
                                {
                                    ispause = true;
                                    break;
                                }
                            }
                        it.SubItems[stateindex].Text = ispause ? str_status_paused : "";
                    }
                    running = ProcessListGetUwpIsRunning(it.Text);
                    if (running && stateindex != -1)
                    {
                        foreach (TaskMgrListItem ix in it.Childs)
                            if (ix.Type == TaskMgrListItemType.ItemProcess && it.SubItems[stateindex].Text == str_status_paused)
                                running = false;
                    }
                    it.Group = running ? listProcess.Groups[0] : listProcess.Groups[1];

                }
                else if (it.Type == TaskMgrListItemType.ItemProcessHost)
                {
                    ProcessListUpdate_State(pid, it, (PsItem)it.Tag);
                    ProcessListUpdate_WindowsAndGroup(pid, it, ((PsItem)it.Tag), isload);
                }

                //Performance 

                if (cpuindex != -1 && ipdateOneDataCloum != cpuindex)
                {
                    double d = 0; int datacount = 0;
                    foreach (TaskMgrListItem ix in it.Childs)
                    {
                        if (ix.Type == TaskMgrListItemType.ItemProcess)
                        {
                            d += ix.SubItems[cpuindex].CustomData;
                            datacount++;
                        }
                    }
                    double ii2 = d;
                    it.SubItems[cpuindex].Text = ii2.ToString("0.0") + "%";
                    it.SubItems[cpuindex].BackColor = ProcessListGetColorFormValue(ii2, 100);
                    it.SubItems[cpuindex].CustomData = ii2;
                }
                if (ramindex != -1 && ipdateOneDataCloum != ramindex)
                {
                    double d = 0;
                    foreach (TaskMgrListItem ix in it.Childs)
                        if (ix.Type == TaskMgrListItemType.ItemProcess)
                            d += ix.SubItems[ramindex].CustomData;
                    it.SubItems[ramindex].Text = FormatFileSizeMen(Convert.ToInt64(d * 1024));
                    it.SubItems[ramindex].BackColor = ProcessListGetColorFormValue(d / 1024, 1024);
                    it.SubItems[ramindex].CustomData = d;
                }
                if (diskindex != -1 && ipdateOneDataCloum != diskindex)
                {
                    double d = 0;
                    foreach (TaskMgrListItem ix in it.Childs)
                        if (ix.Type == TaskMgrListItemType.ItemProcess)
                            d += ix.SubItems[diskindex].CustomData;
                    if (d < 0.1 && d >= PERF_LIMIT_MIN_DATA_DISK) d = 0.1; else d = 0;
                    if (d != 0)
                    {
                        it.SubItems[diskindex].Text = d.ToString("0.0") + " MB/" + str_sec;
                        it.SubItems[diskindex].BackColor = ProcessListGetColorFormValue(d, 1024);
                        it.SubItems[diskindex].CustomData = d;
                    }
                    else
                    {
                        it.SubItems[diskindex].Text = "0 MB/" + str_sec;
                        it.SubItems[diskindex].BackColor = dataGridZeroColor;
                        it.SubItems[diskindex].CustomData = 0;
                    }
                }
                if (netindex != -1 && ipdateOneDataCloum != netindex)
                {
                    double d = 0;
                    foreach (TaskMgrListItem ix in it.Childs)
                        if (ix.Type == TaskMgrListItemType.ItemProcess)
                            d += ix.SubItems[diskindex].CustomData;
                    if (d < 0.1 && d >= PERF_LIMIT_MIN_DATA_NETWORK) d = 0.1; else d = 0;
                    if (d != 0)
                    {
                        it.SubItems[netindex].Text = d.ToString("0.0") + " Mbps";
                        it.SubItems[netindex].CustomData = d;
                        it.SubItems[netindex].BackColor = dataGridZeroColor;
                    }
                    else
                    {
                        it.SubItems[netindex].Text = "0 Mbps";
                        it.SubItems[netindex].CustomData = 0;
                        it.SubItems[netindex].BackColor = dataGridZeroColor;
                    }
                }
            }
            else
            {
                PsItem p = ((PsItem)it.Tag);
                p.SYSTEM_PROCESSES = system_process;
                ProcessListUpdate_WindowsAndGroup(pid, it, p, isload);

                if (stateindex != -1 && ipdateOneDataCloum != stateindex) ProcessListUpdate_State(pid, it, p);
                if (cpuindex != -1 && ipdateOneDataCloum != cpuindex) ProcessListUpdatePerf_Cpu(pid, it, p);
                if (ramindex != -1 && ipdateOneDataCloum != ramindex) ProcessListUpdatePerf_Ram(pid, it, p);
                if (diskindex != -1 && ipdateOneDataCloum != diskindex) ProcessListUpdatePerf_Disk(pid, it, p);
                if (netindex != -1 && ipdateOneDataCloum != netindex) ProcessListUpdatePerf_Net(pid, it, p);
            }
        }
        private void ProcessListUpdateOnePerfCloum(uint pid, TaskMgrListItem it, int ipdateOneDataCloum, bool forceProcessHost = false)
        {
            if (!forceProcessHost && (it.Type == TaskMgrListItemType.ItemUWPHost || it.Type == TaskMgrListItemType.ItemProcessHost))
            {
                TaskMgrListItem ii = it as TaskMgrListItem;
                if (stateindex != -1 && ipdateOneDataCloum == stateindex)
                {
                    if (it.Type == TaskMgrListItemType.ItemUWPHost)
                    {
                        bool running = ProcessListGetUwpIsRunning(it.Text);
                        if (running && stateindex != -1)
                            foreach (TaskMgrListItem ix in it.Childs)
                                if (ix.Type == TaskMgrListItemType.ItemProcess && it.SubItems[stateindex].Text == str_status_paused)
                                    running = false;
                        it.Group = running ? listProcess.Groups[0] : listProcess.Groups[1];
                    }
                    if (stateindex != -1 && ipdateOneDataCloum == stateindex && it.Childs.Count > 0)
                    {
                        bool ispause = false;
                        foreach (TaskMgrListItem ix in it.Childs)
                            if (ix.Type == TaskMgrListItemType.ItemProcess)
                            {
                                PsItem p = ((PsItem)ix.Tag);
                                ProcessListUpdate_State(ix.PID, ix, p);
                                if (ix.SubItems[stateindex].Text == str_status_paused)
                                {
                                    ispause = true;
                                    break;
                                }
                            }
                        it.SubItems[stateindex].Text = ispause ? str_status_paused : "";
                    }
                }
                if (ipdateOneDataCloum > -1)
                {
                    double d = 0; int datacount = 0;
                    foreach (TaskMgrListItem ix in ii.Childs)
                    {
                        if (ix.Type == TaskMgrListItemType.ItemProcess)
                        {
                            ProcessListUpdateOnePerfCloum(ix.PID, ix, ipdateOneDataCloum);
                            d += ix.SubItems[ipdateOneDataCloum].CustomData;
                            datacount++;
                        }
                    }

                    //Performance 
                    if (cpuindex != -1 && ipdateOneDataCloum == cpuindex)
                    {
                        double ii2 = d;// (d / datacount);
                        it.SubItems[cpuindex].Text = ii2.ToString("0.0") + "%";
                        it.SubItems[cpuindex].BackColor = ProcessListGetColorFormValue(ii2, 100);
                        it.SubItems[cpuindex].CustomData = ii2;
                    }
                    else if (ramindex != -1 && ipdateOneDataCloum == ramindex)
                    {
                        it.SubItems[ramindex].Text = FormatFileSizeMen(Convert.ToInt64(d * 1024));
                        it.SubItems[ramindex].BackColor = ProcessListGetColorFormValue(d / 1024, 1024);
                        it.SubItems[ramindex].CustomData = d;
                    }
                    else if (diskindex != -1 && ipdateOneDataCloum == diskindex)
                    {
                        if (d < 0.1 && d >= PERF_LIMIT_MIN_DATA_DISK) d = 0.1; else d = 0;
                        if (d != 0)
                        {
                            it.SubItems[diskindex].Text = d.ToString("0.0") + " MB/" + str_sec;
                            it.SubItems[diskindex].BackColor = ProcessListGetColorFormValue(d, 1024);
                            it.SubItems[diskindex].CustomData = d;
                            return;
                        }
                        it.SubItems[netindex].Text = "0 MB/" + str_sec;
                        it.SubItems[netindex].CustomData = 0;
                        it.SubItems[netindex].BackColor = dataGridZeroColor;
                    }
                    else if (netindex != -1 && ipdateOneDataCloum == netindex)
                    {
                        if (d < 0.1 && d >= PERF_LIMIT_MIN_DATA_NETWORK) d = 0.1; else d = 0;
                        if (d != 0)
                        {
                            it.SubItems[netindex].Text = d.ToString("0.0") + " Mbps";
                            it.SubItems[netindex].CustomData = d;
                            it.SubItems[netindex].BackColor = dataGridZeroColor;
                            return;
                        }
                        it.SubItems[netindex].Text = "0 Mbps";
                        it.SubItems[netindex].CustomData = 0;
                        it.SubItems[netindex].BackColor = dataGridZeroColor;
                    }
                }
            }
            else
            {
                PsItem p = ((PsItem)it.Tag);
                if (stateindex != -1 && ipdateOneDataCloum == stateindex) ProcessListUpdate_State(pid, it, p);
                if (cpuindex != -1 && ipdateOneDataCloum == cpuindex) ProcessListUpdatePerf_Cpu(pid, it, p);
                if (ramindex != -1 && ipdateOneDataCloum == ramindex) ProcessListUpdatePerf_Ram(pid, it, p);
                if (diskindex != -1 && ipdateOneDataCloum == diskindex) ProcessListUpdatePerf_Disk(pid, it, p);
                if (netindex != -1 && ipdateOneDataCloum == netindex) ProcessListUpdatePerf_Net(pid, it, p);
            }
        }

        private int ProcessListUpdate_ChildItemsAdd(TaskMgrListItem it, PsItem p)
        {
            int allCount = 0;
            //递归添加所有子进程
            foreach (PsItem child in p.childs)
            {
                if (!child.isWindowShow)
                {
                    allCount++;
                    if (!it.Childs.Contains(child.item))
                        it.Childs.Add(child.item);
                    if (listProcess.Items.Contains(child.item))
                        listProcess.Items.Remove(child.item);
                    if (child.childs.Count > 0)
                        allCount += ProcessListUpdate_ChildItemsAdd(it, child);
                }
                else if(it.Childs.Contains(child.item))
                {
                    it.Childs.Remove(child.item);
                    if (!listProcess.Items.Contains(child.item))
                        listProcess.Items.Add(child.item);
                }
            }
            return allCount;
        }
        private void ProcessListUpdate_ChildItems(uint pid, TaskMgrListItem it, PsItem p)
        {
            if (p.isWindowShow && p.childs.Count > 0 && !IsExplorer(p) && !it.IsCloneItem)
            {
                if (it.Type != TaskMgrListItemType.ItemProcessHost)
                {
                    it.Type = TaskMgrListItemType.ItemProcessHost;

                    //Clone a child item
                    TaskMgrListItem cloneItem = new TaskMgrListItem();
                    cloneItem.Text = it.Text;
                    cloneItem.PID = it.PID;
                    cloneItem.Type = TaskMgrListItemType.ItemProcess;
                    cloneItem.Tag = p;
                    cloneItem.DisplayChildCount = false;
                    cloneItem.DisplayChildValue = 0;
                    cloneItem.IsCloneItem = true; cloneItem.IsFullData = true;
                    cloneItem.Icon = it.Icon; cloneItem.Image = it.Image;
                    //Copy 10 empty item
                    for (int i = 0; i < 10; i++)
                    {
                        cloneItem.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                        cloneItem.SubItems[i].Text = it.SubItems[i].Text;
                        cloneItem.SubItems[i].Font = it.SubItems[i].Font;
                    }
                    //Make it hight light
                    cloneItem.SubItems[0].ForeColor = Color.FromArgb(0x11, 0x66, 0x00);
                    it.Childs.Add(cloneItem);
                }

                it.DisplayChildCount = true;
                it.DisplayChildValue = ProcessListUpdate_ChildItemsAdd(it, p) + 1;
            }
            else
            {
                if (it.Type != TaskMgrListItemType.ItemProcess)
                    it.Type = TaskMgrListItemType.ItemProcess;

                it.DisplayChildCount = false;

                if (it.Childs.Count > 0)
                {
                    for (int i = it.Childs.Count - 1; i >= 0; i--)
                    {
                        TaskMgrListItem childit = it.Childs[i];
                        if (childit.Type == TaskMgrListItemType.ItemProcess)
                        {
                            it.Childs.Remove(childit);
                            if (!listProcess.Items.Contains(childit))
                                listProcess.Items.Add(childit);
                        }
                    }
                }
            }
        }
        private void ProcessListUpdate_GroupChilds(bool isload, TaskMgrListItem ii, int ipdateOneDataCloum = -1)
        {
            foreach (TaskMgrListItem ix in ii.Childs)
                if (ix.Type == TaskMgrListItemType.ItemProcess)
                    ProcessListUpdate(ix.PID, isload, ix, ((PsItem)ix.Tag).SYSTEM_PROCESSES, ipdateOneDataCloum);
        }

        private void ProcessListUpdate_WindowsAndGroup(uint pid, TaskMgrListItem it, PsItem p, bool isload)
        {
            //Child and group
            if (!p.isSvchost)
            {
                //remove invalid windows
                for (int i = it.Childs.Count - 1; i >= 0; i--)
                {
                    if (it.Childs[i].Type == TaskMgrListItemType.ItemWindow)
                    {
                        IntPtr h = (IntPtr)it.Childs[i].Tag;
                        if (!IsWindow(h) || !IsWindowVisible(h))
                            it.Childs.Remove(it.Childs[i]);
                    }
                }
                if (!isload)
                {
                    //update window
                    thisLoadItem = it;
                    MAppVProcessAllWindowsGetProcessWindow(pid);
                    thisLoadItem = null;

                    int windowCount = 0;
                    for (int i = it.Childs.Count - 1; i >= 0; i--)
                    {
                        if (it.Childs[i].Type == TaskMgrListItemType.ItemWindow)
                            windowCount++;
                    }
                    //group
                    if (windowCount > 0)
                    {
                        p.isWindowShow = true;
                        if (it.Group != listProcess.Groups[0])
                            it.Group = listProcess.Groups[0];
                        ProcessListUpdate_ChildItems(pid, it, p);
                    }
                    else
                    {
                        bool needBreak = false;

                        if (p.isWindowsProcess)
                        {
                            if (it.Group != listProcess.Groups[2])
                            {
                                it.Group = listProcess.Groups[2];
                                needBreak = true;
                            }
                            p.isWindowShow = false;
                        }
                        else
                        {
                            if (it.Group != listProcess.Groups[1])
                            {
                                it.Group = listProcess.Groups[1];
                                needBreak = true;
                            }
                            p.isWindowShow = false;
                        }

                        if (needBreak && it.Childs.Count > 0)
                            ProcessListUpdate_BreakProcHost(it);
                    }
                }
            }
            else
            {
                if (isload)
                {
                    p.isWindowShow = false;
                    it.Group = listProcess.Groups[p.isWindowsProcess ? 2 : 1];
                }
            }
        }
        private void ProcessListUpdate_State(uint pid, TaskMgrListItem it, PsItem p)
        {
            int i = MGetProcessState(p.SYSTEM_PROCESSES, IntPtr.Zero);
            if (i == 1)
            {
                it.SubItems[stateindex].Text = "";
                if (p.isSvchost == false && it.Childs.Count > 0)
                {
                    bool hung = false;
                    foreach (TaskMgrListItem c in it.Childs)
                        if (c.Type == TaskMgrListItemType.ItemWindow)
                            if (IsHungAppWindow((IntPtr)c.Tag))
                            {
                                hung = true;
                                break;
                            }
                    if (hung)
                    {
                        it.SubItems[stateindex].Text = str_status_hung;
                        it.SubItems[stateindex].ForeColor = Color.FromArgb(219, 107, 58);
                    }
                }
            }
            else if (i == 2)
            {
                it.SubItems[stateindex].Text = str_status_paused;
                it.SubItems[stateindex].ForeColor = Color.FromArgb(22, 158, 250);
            }
        }
        private void ProcessListUpdatePerf_Cpu(uint pid, TaskMgrListItem it, PsItem p)
        {
            if (pid != 0 && p.SYSTEM_PROCESSES != IntPtr.Zero)
            {

                double ii = MPERF_GetProcessCpuUseAge(p.SYSTEM_PROCESSES, p.perfData);
                it.SubItems[cpuindex].Text = ii.ToString("0.0") + "%";
                it.SubItems[cpuindex].BackColor = ProcessListGetColorFormValue(ii, 100);
                it.SubItems[cpuindex].CustomData = ii;
            }
            else
            {
                it.SubItems[cpuindex].Text = "0.0%";
                it.SubItems[cpuindex].BackColor = dataGridZeroColor;
                it.SubItems[cpuindex].CustomData = 0;
            }
        }
        private void ProcessListUpdatePerf_Ram(uint pid, TaskMgrListItem it, PsItem p)
        {
            if (p.SYSTEM_PROCESSES != IntPtr.Zero)
            {
                uint ii = MPERF_GetProcessRam(p.SYSTEM_PROCESSES, p.handle);
                it.SubItems[ramindex].Text = FormatFileSizeMen(Convert.ToInt64(ii));
                it.SubItems[ramindex].BackColor = ProcessListGetColorFormValue(ii / 1048576, 1024);
                it.SubItems[ramindex].CustomData = ii / 1024d;
            }
            else if (pid == 4 || pid == 0)
            {
                it.SubItems[ramindex].Text = "0.1 MB";
                it.SubItems[ramindex].BackColor = ProcessListGetColorFormValue(0.1, 1024);
                it.SubItems[ramindex].CustomData = 1;
            }
        }
        private void ProcessListUpdatePerf_Disk(uint pid, TaskMgrListItem it, PsItem p)
        {
            if (p.SYSTEM_PROCESSES != IntPtr.Zero)
            {
                ulong disk = MPERF_GetProcessDiskRate(p.SYSTEM_PROCESSES, p.perfData);
                double val = (disk / 1024d);
                if (val < 0.1 && val >= PERF_LIMIT_MIN_DATA_DISK) val = 0.1; else val = 0;
                if (val != 0)
                {
                    it.SubItems[diskindex].Text = val.ToString("0.0") + " MB/" + str_sec;
                    it.SubItems[diskindex].BackColor = ProcessListGetColorFormValue(disk, 1048576);
                    it.SubItems[diskindex].CustomData = (disk / 1024d);
                    return;
                }
            }

            it.SubItems[diskindex].Text = "0 MB/" + str_sec;
            it.SubItems[diskindex].BackColor = dataGridZeroColor;
            it.SubItems[diskindex].CustomData = 0;
        }
        private void ProcessListUpdatePerf_Net(uint pid, TaskMgrListItem it, PsItem p)
        {
            if (p.updateLock) { p.updateLock = false; return; }
            if (pid > 4 && MPERF_NET_IsProcessInNet(pid))
            {
                double allMBytesPerSec = MPERF_GetProcessNetWorkRate(pid, p.perfData) / 1048576d;
                if (allMBytesPerSec < 0.1 && allMBytesPerSec >= PERF_LIMIT_MIN_DATA_NETWORK) allMBytesPerSec = 0.1; else allMBytesPerSec = 0;
                if (allMBytesPerSec != 0)
                {
                    it.SubItems[netindex].Text = allMBytesPerSec.ToString("0.0") + " Mbps";
                    it.SubItems[netindex].CustomData = allMBytesPerSec;
                    it.SubItems[netindex].BackColor = dataGridZeroColor;
                    return;
                }
            }

            it.SubItems[netindex].Text = "0 Mbps";
            it.SubItems[netindex].CustomData = 0;
            it.SubItems[netindex].BackColor = dataGridZeroColor;
        }
        private void ProcessListUpdate_BreakProcHost(TaskMgrListItem it)
        {
            if (it.Group != listProcess.Groups[0])
            {
                if (it.Childs.Count > 0)
                {
                    foreach (TaskMgrListItem lics in it.Childs)
                        if (lics.Type == TaskMgrListItemType.ItemProcess && !lics.IsCloneItem)
                            listProcess.Items.Add(lics);
                }
            }
        }

        private void ProcessListPrepareClear()
        {
            //clear valid Pids
            validPid.Clear();
            foreach (PsItem p in loadedPs) p.SYSTEM_PROCESSES = IntPtr.Zero;
        }
        private void ProcessListClear()
        {
            //清除validPid里没有的项目
            uint pid = 0;
            for (int i = loadedPs.Count - 1; i >= 0; i--)
            {
                pid = loadedPs[i].pid;
                if (!validPid.Contains(pid))
                    ProcessListFree(loadedPs[i]);
            }
            //存在的项目则从validPid里清除
            foreach (TaskMgrListItem i in listProcess.Items)
                if (i.Type == TaskMgrListItemType.ItemUWPHost || i.Type == TaskMgrListItemType.ItemProcessHost)
                {
                    foreach (TaskMgrListItem ix in i.Childs)
                        validPid.Remove(ix.PID);
                    if (i.PID != 1)
                        validPid.Remove(i.PID);
                }
                else validPid.Remove(i.PID);
            //如果validPid里还有项目，则将其添加
            if (validPid.Count > 0)
            {
                for (int i = validPid.Count - 1; i >= 0; i--)
                    MReUpdateProcess(validPid[i], enumProcessCallBack_ptr);
            }
        }
        private void ProcessListFree(PsItem it)
        {
            //remove invalid item
            TaskMgrListItem li = ProcessListFindItem(it.pid);
            MCloseHandle(it.handle);

            uwphostitem hostitem = ProcessListFindUWPItemWithHostId(it.pid);
            if (hostitem != null) uwpHostPid.Remove(hostitem);

            MPERF_PerfDataDestroy(it.perfData);
            it.svcs.Clear();
            it.childs.Clear();
            if (it.parent != null && it.parent.childs.Contains(it))
                it.parent.childs.Remove(it);
            it.parent = null;
            if (it.uwpItem != null)
                it.uwpItem = null;

            loadedPs.Remove(it);

            if (li != null)
            {
                //is a group item
                if (li.Type == TaskMgrListItemType.ItemUWPHost || li.Type == TaskMgrListItemType.ItemProcessHost)
                {
                    ProcessListUpdate_BreakProcHost(li);
                    listProcess.Items.Remove(li);
                }
                else
                {
                    if (li.Parent != null)//is a child item
                    {
                        TaskMgrListItem iii = li.Parent;
                        iii.Childs.Remove(li);
                        if (iii.Type == TaskMgrListItemType.ItemUWPHost)
                        {
                            if (iii.Childs.Count == 0)//o to remove
                            {
                                listProcess.Items.Remove(iii);
                                uwpitem parentItem = ProcessListFindUWPItem(iii.Tag.ToString());
                                if (parentItem != null) uwps.Remove(parentItem);
                            }
                            else if (iii.Childs.Count > 1)
                            {
                                //update (x) child item count
                                string text = iii.Text;
                                if (text.Contains("(") && text.EndsWith(")"))
                                {
                                    text = text.Remove(text.Length - 3);
                                    iii.Text = text + " (" + iii.Childs.Count + ")";
                                }
                            }
                        }
                    }
                    else listProcess.Items.Remove(li);
                }
            }
        }
        private void ProcessListFreeAll()
        {
            //the exit clear
            uwps.Clear();
            uwpHostPid.Clear();
            for (int i = 0; i < loadedPs.Count; i++)
                ProcessListFree(loadedPs[i]);
            loadedPs.Clear();
            listProcess.Items.Clear();
        }

        private void ProcessListHandle(uint pid, uint ppid, IntPtr name, IntPtr exefullpath, int tp, IntPtr hprocess, IntPtr system_process)
        {
            //enum proc callback
            if (tp == 1)
            {
                if (!isRunAsAdmin && exefullpath == IntPtr.Zero && pid != 0 && pid != 4 && pid != 88)
                    return;
                validPid.Add(pid);
                PsItem item;
                if (ProcessListIsProcessLoaded(pid, out item))
                    ProcessListUpdate(pid, false, item.item, system_process);//在列表里，刷新
                else//没有在列表里，添加
                    ProcessListLoad(pid, ppid, Marshal.PtrToStringAuto(name), Marshal.PtrToStringAuto(exefullpath), hprocess, system_process);
            }
            else if (tp == 0)//完成
                ProcessListRefesh1Finished();
        }
        private void ProcessListHandle2(uint pid, IntPtr system_process)
        {
            //enum proc callback2 (refesh)
            validPid.Add(pid);
            //刷新已有项目的system_process属性
            PsItem old = ProcessListFindPsItem(pid);
            if (old != null) old.SYSTEM_PROCESSES = system_process;//更新此属性，因为刷新性能数据有用
        }

        private void ProcessListUpdateValues(int refeshAllDataColum)
        {
            //update process perf data

            if (!isSimpleView)
            {
                foreach (TaskMgrListItem it in listProcess.Items)
                {
                    if (refeshAllDataColum != -1)
                        ProcessListUpdateOnePerfCloum(it.PID, it, refeshAllDataColum);
                }
                for (int i = 0; i < listProcess.ShowedItems.Count; i++)
                {
                    if (listProcess.ShowedItems[i].Parent != null) continue;
                    //只刷新显示的条目
                    if (listProcess.ShowedItems[i].Type == TaskMgrListItemType.ItemUWPHost)
                        ProcessListUpdate(listProcess.ShowedItems[i].PID, false, listProcess.ShowedItems[i], IntPtr.Zero, refeshAllDataColum);
                    else ProcessListUpdate(listProcess.ShowedItems[i].PID, false, listProcess.ShowedItems[i], ((PsItem)listProcess.ShowedItems[i].Tag).SYSTEM_PROCESSES, refeshAllDataColum);
                }
            }
        }
    
        private void ProcessListLoadFinished()
        {
            //firstLoad
            firstLoad = false;
            listProcess.Show();
            Cursor = Cursors.Arrow;
        }
        private void ProcessListEndTask(uint pid, TaskMgrListItem taskMgrListItem)
        {
            //结束任务
            if (taskMgrListItem == null) taskMgrListItem = ProcessListFindItem(pid);
            if (taskMgrListItem != null)
            {
                if (taskMgrListItem.Type == TaskMgrListItemType.ItemProcessHost)
                {
                    bool ananyrs = false;
                    PsItem p = taskMgrListItem.Tag as PsItem;
                    if (p.isWindowShow && !p.isSvchost)
                    {
                        if (taskMgrListItem.Childs.Count > 0)
                        {
                            IntPtr target = IntPtr.Zero;
                            for (int i = taskMgrListItem.Childs.Count - 1; i >= 0; i--)
                                if (taskMgrListItem.Childs[i].Type == TaskMgrListItemType.ItemWindow)
                                {
                                    target = (IntPtr)taskMgrListItem.Childs[i].Tag;
                                    if (target != IntPtr.Zero)
                                        if (MAppWorkCall3(192, IntPtr.Zero, target) == 1)
                                            ananyrs = true;
                                }
                        }
                        else ananyrs = true;
                    }

                    if (!ananyrs)
                        nextKillItem = taskMgrListItem;
                    else
                    {
                        foreach (TaskMgrListItem lichild in taskMgrListItem.Childs)
                        {
                            if (lichild.Type == TaskMgrListItemType.ItemProcess)
                                if (!MKillProcessUser2(lichild.PID, false))
                                    break;
                        }
                        MKillProcessUser2(taskMgrListItem.PID, true);
                    }
                }
                else if (taskMgrListItem.Type == TaskMgrListItemType.ItemUWPHost)
                {
                    foreach (TaskMgrListItem lichild in taskMgrListItem.Childs)
                    {
                        if (lichild.Type == TaskMgrListItemType.ItemProcess)
                            MKillProcessUser2(lichild.PID, true);
                    }
                }
                else if (taskMgrListItem.Type == TaskMgrListItemType.ItemProcess)
                {
                    bool ananyrs = false;
                    PsItem p = taskMgrListItem.Tag as PsItem;
                    if (p.isWindowShow && !p.isSvchost)
                    {
                        if (taskMgrListItem.Childs.Count > 0)
                        {
                            IntPtr target = IntPtr.Zero;
                            for (int i = taskMgrListItem.Childs.Count - 1; i >= 0; i--)
                                if (taskMgrListItem.Childs[i].Tag != null)
                                { 
                                    target = (IntPtr)taskMgrListItem.Childs[i].Tag;
                                    if (target != IntPtr.Zero)
                                        if (MAppWorkCall3(192, IntPtr.Zero, target) == 1)
                                            ananyrs = true;
                                }
                        }
                        else ananyrs = true;
                    }
                    if (ananyrs) MKillProcessUser2(taskMgrListItem.PID, true);
                }
            }
        }
        private void ProcessListSetTo(TaskMgrListItem taskMgrListItem)
        {
            //设置到
            if (taskMgrListItem != null)
            {
                PsItem p = taskMgrListItem.Tag as PsItem;
                if (p.isWindowShow && !p.isSvchost)
                {
                    if (taskMgrListItem.Childs.Count > 0)
                    {
                        IntPtr target = IntPtr.Zero;
                        foreach (TaskMgrListItemChild c in taskMgrListItem.Childs)
                            if (c.Tag != null)
                            {
                                target = (IntPtr)c.Tag;
                                break;
                            }
                        if (target != IntPtr.Zero) MAppWorkCall3(213, target, target) ;
                    }
                }
            }
        }
        private void ProcessListKillLastEndItem()
        {
            if (nextKillItem != null)
            {
                if (listProcess.Items.Contains(nextKillItem))
                {
                    foreach (TaskMgrListItem lichild in nextKillItem.Childs)
                    {
                        if (lichild.Type == TaskMgrListItemType.ItemProcess)
                            if (!MKillProcessUser2(lichild.PID, false))
                                break;
                    }
                    MKillProcessUser2(nextKillItem.PID, true);
                }
                nextKillItem = null;
            }
        }

        private void ProcessListSimpleInit()
        {
            listApps.NoHeader = true;
            expandFewerDetals.Show();
            expandFewerDetals.Expanded = true;

            isSimpleView = GetConfigBool("SimpleView", "AppSetting", true);
        }
        private void ProcessListSimpleExit()
        {
            if (isSimpleView)
            {
                lastSimpleSize = Size;
            }
            SetConfig("OldSizeSimple", "AppSetting", lastSimpleSize.Width + "-" + lastSimpleSize.Height);
            SetConfigBool("SimpleView", "AppSetting", isSimpleView);
        }
        private void ProcessListSimpleRefesh()
        {
            listApps.Locked = true;
            listApps.Items.Clear();
            foreach (TaskMgrListItem li in listProcess.Items)
            {
                if (li.Group == listProcess.Groups[0])
                {
                    if (li.Type == TaskMgrListItemType.ItemProcess)
                        if (IsExplorer((PsItem)li.Tag))
                            continue;
                    if (li.PID != currentProcessPid)
                        listApps.Items.Add(li);
                }
            }
            listApps.Locked = false;
            listApps.SyncItems(true);
            listApps_SelectItemChanged(null, null);
        }

        private void check_showAllProcess_CheckedChanged(object sender, EventArgs e)
        {
            //switch to admin
            //显示所有进程（切换到管理员模式）
            if (!MIsRunasAdmin()) {
                if (check_showAllProcess.Checked)
                {
                    MAppRebotAdmin();
                    check_showAllProcess.Checked = false;
                }
                else check_showAllProcess.Checked = false;
            }
            else check_showAllProcess.Hide();
        }
        private void lbShowDetals_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            //if (!MAppVProcess(Handle)) TaskDialog.Show("无法打开详细信息窗口", str_AppTitle, "未知错误。", TaskDialogButton.OK, TaskDialogIcon.Stop);
        }
        private void expandFewerDetals_Click(object sender, EventArgs e)
        {
            if (!isSimpleView)
            {
                lastSize = Size;
                isSimpleView = true;
                if (Size.Width > lastSimpleSize.Width || Size.Height > lastSimpleSize.Height)
                    Size = lastSimpleSize;
            }
        }
        private void expandMoreDetals_Click(object sender, EventArgs e)
        {
            if (isSimpleView)
            {
                lastSimpleSize = Size;
                isSimpleView = false;
                if (Size.Width < lastSize.Width || Size.Height < lastSize.Height)
                    Size = lastSize;
            }
        }

        private void btnEndTaskSimple_Click(object sender, EventArgs e)
        {
            TaskMgrListItem taskMgrListItem = listApps.SelectedItem;
            if (taskMgrListItem != null)
                ProcessListEndTask(0, taskMgrListItem);
        }
        private void btnEndProcess_Click(object sender, EventArgs e)
        {
            TaskMgrListItem taskMgrListItem = listProcess.SelectedItem;
            if (taskMgrListItem != null)
                ProcessListEndTask(0, taskMgrListItem);
        }

        private void BaseProcessRefeshTimerLowSc_Tick(object sender, EventArgs e)
        {
            if (tabControlMain.SelectedTab == tabPageProcCtl)
                ScMgrRefeshList();
        }
        private void BaseProcessRefeshTimerLow_Tick(object sender, EventArgs e)
        {
            refeshLowLock = true;
            if (tabControlMain.SelectedTab == tabPageProcCtl)
                ProcessListForceRefeshAll();
            refeshLowLock = false;
        }

        #region ListEvents

        private void listApps_SelectItemChanged(object sender, EventArgs e)
        {
            btnEndTaskSimple.Enabled = listApps.SelectedItem != null;
        }
        private void listApps_KeyDown(object sender, KeyEventArgs e)
        {
            TaskMgrListItem li = listApps.SelectedItem;
            if (li == null) return;
            if (e.KeyCode == Keys.Delete)
                ProcessListEndTask(0, li);
            else if (e.KeyCode == Keys.Apps)
            {
                Point p = listApps.GetiItemPoint(li);
                p = listApps.PointToScreen(p);
                MAppWorkCall3(212, new IntPtr(p.X), new IntPtr(p.Y));
                MAppWorkCall3(214, Handle, IntPtr.Zero);
            }
        }
        private void listApps_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (listApps.SelectedItem != null)
                {
                    MAppWorkCall3(212, new IntPtr(MousePosition.X), new IntPtr(MousePosition.Y));
                    MAppWorkCall3(214, Handle, IntPtr.Zero);
                }
            }
        }
        private void listApps_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                TaskMgrListItem li = listApps.SelectedItem;
                if (li == null) return;
                ProcessListSetTo(li);
            }
        }

        private void listProcess_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            TaskMgrListItem li = listProcess.SelectedItem;
            if (li == null) return;
            if (e.Button == MouseButtons.Left)
            {
                if (li.OldSelectedItem != null)
                {
                    if (li.OldSelectedItem.Type == TaskMgrListItemType.ItemWindow && li.OldSelectedItem.Tag != null)
                    {
                        IntPtr data = (IntPtr)li.OldSelectedItem.Tag;
                        MAppWorkCall3(213, data, IntPtr.Zero);
                        WindowState = FormWindowState.Minimized;
                    }
                }
                else if (li.Type == TaskMgrListItemType.ItemWindow)
                {
                    if (li.Tag != null)
                    {
                        IntPtr data = (IntPtr)li.Tag;
                        MAppWorkCall3(213, data, IntPtr.Zero);
                        WindowState = FormWindowState.Minimized;
                    }
                }
                else if (li.Childs.Count > 0)
                {
                    li.ChildsOpened = !li.ChildsOpened;
                    listProcess.SyncItems(true);
                }
            }
        }
        private void listProcess_ShowMenuSelectItem(Point pos = default(Point))
        {
            TaskMgrListItem selectedItem = listProcess.SelectedItem.OldSelectedItem == null ?
                 listProcess.SelectedItem : listProcess.SelectedItem.OldSelectedItem;
            if (selectedItem.Type == TaskMgrListItemType.ItemProcess 
                || selectedItem.Type == TaskMgrListItemType.ItemUWPProcess
                || selectedItem.Type == TaskMgrListItemType.ItemProcessHost)
            {
                PsItem t = (PsItem)selectedItem.Tag;
                int rs = MAppWorkShowMenuProcess(t.exepath, selectedItem.Text, t.pid, Handle, isSelectExplorer ? 1 : 0, nextSecType, pos.X, pos.Y);
            }
            else if (selectedItem.Type == TaskMgrListItemType.ItemUWPHost)
            {
                uwpitem t = (uwpitem)selectedItem.Tag;
                MAppWorkShowMenuProcess(t.uwpInstallDir, t.uwpFullName, 1, Handle, 0, nextSecType, pos.X, pos.Y);
            }
            else if (selectedItem.Type == TaskMgrListItemType.ItemWindow)
            {
                MAppWorkCall3(212, new IntPtr(pos.X), new IntPtr(pos.Y));
                MAppWorkCall3(189, Handle, (IntPtr)selectedItem.Tag);
            }
            else if (selectedItem.Type == TaskMgrListItemType.ItemService)
            {
                IntPtr scname = Marshal.StringToHGlobalUni((string)selectedItem.Tag);
                MAppWorkCall3(212, new IntPtr(pos.X), new IntPtr(pos.Y));
                MAppWorkCall3(184, Handle, scname);
                Marshal.FreeHGlobal(scname);
            }
        }
        private void listProcess_PrepareShowMenuSelectItem()
        {
            TaskMgrListItem selectedItem = listProcess.SelectedItem.OldSelectedItem == null ?
                listProcess.SelectedItem : listProcess.SelectedItem.OldSelectedItem;
            if (selectedItem.Type== TaskMgrListItemType.ItemProcess 
                || selectedItem.Type == TaskMgrListItemType.ItemUWPProcess
                || selectedItem.Type == TaskMgrListItemType.ItemProcessHost)
            {
                PsItem t = (PsItem)selectedItem.Tag;
                if (t.pid > 4)
                {
                    btnEndProcess.Enabled = true;
                    MAppWorkShowMenuProcessPrepare(t.exepath, t.exename, t.pid, IsImporant(t), IsVeryImporant(t));

                    if (IsExplorer(t))
                    {
                        nextSecType = MENU_SELECTED_PROCESS_KILL_ACT_REBOOT;
                        btnEndProcess.Text = str_resrat;
                        isSelectExplorer = true;
                    }
                    else
                    {
                        if (t.isWindowShow)
                        {
                            if (stateindex != -1)
                            {
                                string s = listProcess.SelectedItem.SubItems[stateindex].Text;
                                if (s == str_status_paused || s == str_status_hung)
                                {
                                    btnEndProcess.Text = str_endproc;
                                    nextSecType = MENU_SELECTED_PROCESS_KILL_ACT_KILL;
                                    goto OUT;
                                }
                            }

                            btnEndProcess.Text = str_endtask;
                            nextSecType = MENU_SELECTED_PROCESS_KILL_ACT_RESENT_BACK;

                        }
                        else
                        {
                            btnEndProcess.Text = str_endproc;
                            nextSecType = MENU_SELECTED_PROCESS_KILL_ACT_KILL;
                        }
                        OUT:
                        isSelectExplorer = false;
                    }
                }
                else btnEndProcess.Enabled = false;
            }
            else if (selectedItem.Type == TaskMgrListItemType.ItemUWPHost)
            {
                nextSecType = MENU_SELECTED_PROCESS_KILL_ACT_UWP_RESENT_BACK;
                string exepath = selectedItem.Tag.ToString();
                MAppWorkShowMenuProcessPrepare(exepath, null, 0, false, false);
                btnEndProcess.Text = str_endtask;
                btnEndProcess.Enabled = true;
            }
            else if (selectedItem.Type == TaskMgrListItemType.ItemWindow)
            {
                MAppWorkCall3(198, IntPtr.Zero, (IntPtr)selectedItem.Tag);
            }
            else if (selectedItem.Type == TaskMgrListItemType.ItemService)
            {
                IntPtr scname = Marshal.StringToHGlobalUni((string)selectedItem.Tag);
                MAppWorkCall3(197, IntPtr.Zero, scname);
                Marshal.FreeHGlobal(scname);
            }
        }
        private void listProcess_MouseClick(object sender, MouseEventArgs e)
        {
            if (listProcess.SelectedItem == null) return;
            if (e.Button == MouseButtons.Right)
            {
                listProcess_PrepareShowMenuSelectItem();
                listProcess_ShowMenuSelectItem();
            }
        }
        private void listProcess_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
                listProcess_PrepareShowMenuSelectItem();
        }
        private void listProcess_SelectItemChanged(object sender, EventArgs e)
        {
            if (listProcess.SelectedItem == null)
                btnEndProcess.Enabled = false;
        }
        private void listProcess_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                btnEndProcess_Click(sender, e);
            }
            else if (e.KeyCode == Keys.Apps)
            {
                if (listProcess.SelectedItem != null)
                {
                    Point p = listProcess.GetiItemPoint(listProcess.SelectedItem);

                    listProcess_PrepareShowMenuSelectItem();
                    listProcess_ShowMenuSelectItem(listProcess.PointToScreen(p));
                }
            }
        }

        private void Header_CloumClick(object sender, TaskMgrListHeader.TaskMgrListHeaderEventArgs e)
        {
            if (e.MouseEventArgs.Button == MouseButtons.Left)
            {
                listProcess.Locked = true;
                if (e.Item.ArrowType == TaskMgrListHeaderSortArrow.None)
                {
                    lvwColumnSorter.Order = SortOrder.Ascending;
                    sortitem = e.Index;
                    sorta = true;
                }
                else if (e.Item.ArrowType == TaskMgrListHeaderSortArrow.Ascending)
                {
                    lvwColumnSorter.Order = SortOrder.Ascending;
                    sortitem = e.Index;
                    sorta = true;
                }
                else if (e.Item.ArrowType == TaskMgrListHeaderSortArrow.Descending)
                {
                    lvwColumnSorter.Order = SortOrder.Descending;
                    sortitem = e.Index;
                    sorta = false;
                }
                lvwColumnSorter.SortColumn = e.Index;
                listProcess.ListViewItemSorter = lvwColumnSorter;
                if (0 == lvwColumnSorter.SortColumn)
                    listProcess.ShowGroup = true;
                else listProcess.ShowGroup = false;
                listProcess.Sort();
                listProcess.Locked = false;
                listProcess.Invalidate();
            }
        }

        private class TaskListViewColumnSorter : ListViewColumnSorter
        {
            private FormMain m;

            public TaskListViewColumnSorter(FormMain m)
            {
                this.m = m;
            }
            public override int Compare(TaskMgrListItem x, TaskMgrListItem y)
            {
                int compareResult = 0;

                if (SortColumn == 0) compareResult = string.Compare(x.Text, y.Text);
                else if (SortColumn == m.cpuindex
                    || SortColumn == m.ramindex
                    || SortColumn == m.diskindex
                    || SortColumn == m.netindex)
                    compareResult = ObjectCompare.Compare(x.SubItems[SortColumn].CustomData, y.SubItems[SortColumn].CustomData);
                else if (SortColumn == m.pidindex)
                    compareResult = ObjectCompare.Compare(x.PID, y.PID);
                else compareResult = string.Compare(x.SubItems[SortColumn].Text, y.SubItems[SortColumn].Text);

                if (compareResult == 0)
                    compareResult = ObjectCompare.Compare(x.PID, y.PID);
                if (Order == SortOrder.Ascending)
                    return compareResult;
                else if (Order == SortOrder.Descending)
                    return (-compareResult);
                return compareResult;
            }
        }

        #endregion

        #region Headers
        public class itemheader
        {
            public itemheader(int index, string name, int wi)
            {
                this.index = index;
                this.name = name;
                width = wi;
                show = true;
            }

            public int width = 0;
            public bool show = false;
            public int index = 0;
            public string name = "";
        }
        public bool saveheader = true;
        List<itemheader> headers = new List<itemheader>();
        int currHeaderI = 0;
        private void listProcessAddHeader(string name, int width)
        {
            headers.Add(new itemheader(currHeaderI, name, width));
            currHeaderI++;
            TaskMgrListHeaderItem li = new TaskMgrListHeaderItem();
            li.TextSmall = LanuageMgr.GetStr(name);
            li.Identifier = name;
            li.Width = width;
            listProcess.Colunms.Add(li);
        }
        private int listProcessGetListIndex(string name)
        {
            int rs = -1;
            for (int i = 0; i < headers.Count; i++)
            {
                if (headers[i].name == name)
                {
                    if (headers[i].show)
                    {
                        rs = headers[i].index;
                    }
                    break;
                }
            }
            return rs;
        }
        public itemheader listProcessGetListHeaderItem(string name)
        {
            itemheader rs = null;
            for (int i = 0; i < headers.Count; i++)
            {
                if (headers[i].name == name)
                {
                    rs = headers[i];
                    break;
                }
            }
            return rs;
        }

        int nameindex = 0;
        int companyindex = 0;
        int stateindex = 0;
        int pidindex = 0;
        int cpuindex = 0;
        int ramindex = 0;
        int diskindex = 0;
        int netindex = 0;
        int pathindex = 0;
        int cmdindex = 0;
        int eprocessindex = 0;
        #endregion

        #endregion

        #region FileMgrWork

        private Dictionary<string, string> fileTypeNames = new Dictionary<string, string>();
        private TreeNode lastClickTreeNode = null;
        private string lastShowDir = "";
        private bool lastRightClicked = false;

        private void FileMgrInit()
        {
            if (!fileListInited)
            {
                fileListInited = true;

                fileMgrCallBack = FileMgrCallBack;
                MFM_SetCallBack(Marshal.GetFunctionPointerForDelegate(fileMgrCallBack));

                imageListFileMgrLeft.Images.Add("folder", Icon.FromHandle(MFM_GetFolderIcon()));
                imageListFileMgrLeft.Images.Add("mycp", Icon.FromHandle(MFM_GetMyComputerIcon()));

                imageListFileTypeList.Images.Add("folder", Icon.FromHandle(MFM_GetFolderIcon()));

                MAppWorkCall3(182, treeFmLeft.Handle, IntPtr.Zero);
                MAppWorkCall3(182, listFm.Handle, IntPtr.Zero);

                string smycp = Marshal.PtrToStringAuto(MFM_GetMyComputerName());
                treeFmLeft.Nodes.Add("mycp", smycp, "mycp", "mycp").Tag = "mycp";
                MFM_GetRoots();
            }
        }
        private IntPtr FileMgrCallBack(int msg, IntPtr lParam, IntPtr wParam)
        {
            switch (msg)
            {
                case 2:
                    {
                        string s = Marshal.PtrToStringAuto(lParam);
                        string path = Marshal.PtrToStringAuto(wParam);
                        Icon icon = Icon.FromHandle(MFM_GetFileIcon(path, null, 0));
                        imageListFileMgrLeft.Images.Add(path, icon);
                        imageListFileTypeList.Images.Add(path, icon);
                        TreeNode n = treeFmLeft.Nodes[0].Nodes.Add(path, s, path, path);
                        n.Tag = path;
                        n.Nodes.Add("loading", str_loading, "loading", "loading");
                        break;
                    }
                case 3:
                    {
                        if (wParam.ToInt32() == -1)
                        {
                            lastClickTreeNode.Nodes[0].Text = str_VisitFolderFailed;
                            lastClickTreeNode.Nodes[0].ImageKey = "err";
                        }
                        else
                        {
                            string s = Marshal.PtrToStringAuto(lParam);
                            string path = Marshal.PtrToStringAuto(wParam);
                            TreeNode n = lastClickTreeNode.Nodes.Add(s, s, "folder", "folder");
                            if (path.EndsWith("\\"))
                                n.Tag = path + s;
                            else n.Tag = path + "\\" + s;
                            n.Nodes.Add("loading", str_loading, "loading", "loading");
                        }
                        break;
                    }
                case 5:
                    {
                        string s = Marshal.PtrToStringAuto(lParam);
                        string path = Marshal.PtrToStringAuto(wParam);
                        listFm.Items.Add(new ListViewItem(s, "folder") { Tag = path.EndsWith("\\") ? path + s : path + "\\" + s });
                        break;
                    }
                case 6:
                case 26:
                    {
                        if (wParam.ToInt32() == -1)
                        {
                            listFm.Items.Clear();
                            string path = Marshal.PtrToStringAuto(lParam);
                            listFm.Items.Add(new ListViewItem("..", "folder") { Tag = "..\\back\\" + path });
                            ListViewItem lvi = listFm.Items.Add(str_VisitFolderFailed, "err");
                        }
                        else
                        {
                            ListViewItem it = null;
                            WIN32_FIND_DATA data = default(WIN32_FIND_DATA);
                            data = (WIN32_FIND_DATA)Marshal.PtrToStructure(lParam, data.GetType());
                            string s = data.cFileName;
                            string path = Marshal.PtrToStringAuto(wParam);
                            string fpath = path + "\\" + s;
                            fpath = fpath.Replace("\\\\", "\\");
                            string fext = "*" + Path.GetExtension(fpath);
                            if (fext == "") fext = "*.*";
                            if (fext == "*.exe")
                            {
                                if (!imageListFileTypeList.Images.ContainsKey(fpath) && MFM_FileExist(fpath))
                                {
                                    StringBuilder sb0 = new StringBuilder(260);
                                    IntPtr h = MGetExeIcon(fpath);
                                    if (h != IntPtr.Zero)
                                        imageListFileTypeList.Images.Add(fpath, Icon.FromHandle(h));
                                    if (!fileTypeNames.ContainsKey(fpath))
                                    {
                                        MGetExeDescribe(fpath, sb0, 260);
                                        fileTypeNames.Add(fpath, sb0.ToString());
                                    }
                                    sb0 = null;
                                }
                                if (msg == 26)
                                {
                                    foreach (ListViewItem i in listFm.Items)
                                    {
                                        if (i.Tag.ToString() == fpath)
                                        {
                                            it = i;
                                            break;
                                        }
                                    }
                                }
                                else
                                {
                                    it = listFm.Items.Add(new ListViewItem(s, fpath) { Tag = fpath });
                                }
                                string typeName = "";
                                if (fileTypeNames.TryGetValue(fpath, out typeName))
                                    it.SubItems.Add(typeName);
                                else it.SubItems.Add("");
                            }
                            else
                            {
                                if (!imageListFileTypeList.Images.ContainsKey(fext))
                                {
                                    StringBuilder sb0 = new StringBuilder(80);
                                    imageListFileTypeList.Images.Add(fext, Icon.FromHandle(MFM_GetFileIcon(fext, sb0, 80)));
                                    if (!fileTypeNames.ContainsKey(fext))
                                        fileTypeNames.Add(fext, sb0.ToString());
                                    else imageListFileTypeList.Images.Add(fext, Icon.FromHandle(MFM_GetFileIcon(fext, null, 0)));
                                    sb0 = null;
                                }
                                if (msg == 26)
                                {
                                    foreach (ListViewItem i in listFm.Items)
                                    {
                                        if (i.Tag.ToString() == fpath)
                                        {
                                            it = i;
                                            break;
                                        }
                                    }
                                }
                                else
                                {
                                    it = listFm.Items.Add(new ListViewItem(s, fext) { Tag = fpath });
                                }

                                string typeName = "";
                                if (fileTypeNames.TryGetValue(fext, out typeName))
                                    it.SubItems.Add(typeName);
                                else it.SubItems.Add("");
                            }

                            long size = (data.nFileSizeHigh * 0xffffffff + 1) + data.nFileSizeLow;
                            it.SubItems.Add(FormatFileSize(size));

                            StringBuilder sb = new StringBuilder(26);
                            if (MFM_GetFileTime(ref data.ftCreationTime, sb, 26))
                                it.SubItems.Add(sb.ToString());
                            else it.SubItems.Add("Unknow");

                            StringBuilder sb2 = new StringBuilder(26);
                            if (MFM_GetFileTime(ref data.ftLastWriteTime, sb2, 26))
                                it.SubItems.Add(sb2.ToString());
                            else it.SubItems.Add("Unknow");

                            StringBuilder sb3 = new StringBuilder(32);
                            bool hidden = false;
                            if (MFM_GetFileAttr(data.dwFileAttributes, sb3, 32, ref hidden))
                            {
                                if (hidden)
                                {
                                    it.ForeColor = Color.Gray;
                                    it.SubItems[0].ForeColor = Color.Gray;
                                    it.SubItems[1].ForeColor = Color.Gray;
                                    it.SubItems[2].ForeColor = Color.Gray;
                                    it.SubItems[3].ForeColor = Color.Gray;
                                }
                                it.SubItems.Add(sb3.ToString());
                            }
                            else it.SubItems.Add("");

                        }
                        break;
                    }
                case 7:
                    {
                        string path = Marshal.PtrToStringAuto(wParam);
                        listFm.Items.Add(new ListViewItem("..", "folder") { Tag = "..\\back\\" + path });
                        break;
                    }
                case 8:
                    FileMgrShowFiles(null);
                    break;
                case 9:
                    {
                        if (listFm.SelectedItems.Count > 0)
                        {
                            ListViewItem listViewItem = listFm.SelectedItems[0];
                            string path = listViewItem.Tag.ToString();
                            listViewItem.BeginEdit();
                            currEditingItem = listViewItem;
                        }
                        break;
                    }
                case 10:
                    {
                        ListViewItem listViewItem = listFm.Items.Add(LanuageMgr.GetStr("NewFolder"), "folder");
                        listViewItem.Tag = "newfolder";
                        listViewItem.BeginEdit();
                        currEditingItem = listViewItem;
                        break;
                    }
                case 11:
                    {
                        foreach (ListViewItem i in listFm.Items)
                            i.Selected = true;
                        break;
                    }
                case 12:
                    {
                        foreach (ListViewItem i in listFm.Items)
                            i.Selected = false;
                        break;
                    }
                case 13:
                    {
                        foreach (ListViewItem i in listFm.Items)
                            i.Selected = !i.Selected;
                        break;
                    }
                case 14:
                    lbFileMgrStatus.Text = Marshal.PtrToStringAuto(lParam);
                    break;
                case 15:
                    switch (lParam.ToInt32())
                    {
                        case 0: lbFileMgrStatus.Text = str_Ready; break;
                        case 1:
                            {
                                if (listFm.SelectedItems.Count > 0)
                                    lbFileMgrStatus.Text = str_ReadyStatus + listFm.Items.Count + str_ReadyStatusEnd2 + listFm.SelectedItems.Count + str_ReadyStatusEnd;
                                else lbFileMgrStatus.Text = str_ReadyStatus + listFm.Items.Count + str_ReadyStatusEnd;
                                break;
                            }
                        case 2: lbFileMgrStatus.Text = ""; break;
                        case 3: lbFileMgrStatus.Text = ""; break;
                        case 4: lbFileMgrStatus.Text = ""; break;
                        case 5: lbFileMgrStatus.Text = str_FileCuted; break;
                        case 6: lbFileMgrStatus.Text = str_FileCopyed; break;
                        case 7: lbFileMgrStatus.Text = str_NewFolderFailed; break;
                        case 8: lbFileMgrStatus.Text = str_NewFolderSuccess; break;
                        case 9: lbFileMgrStatus.Text = str_PathCopyed; break;
                        case 10: lbFileMgrStatus.Text = str_FolderCuted; break;
                        case 11: lbFileMgrStatus.Text = str_FolderCopyed; break;
                    }

                    break;
                case 16:
                    int index = lParam.ToInt32();
                    if (index > 0 && index < listFm.SelectedItems.Count)
                        return Marshal.StringToHGlobalAuto(listFm.SelectedItems[index].Tag.ToString());
                    break;
                case 17:
                    if (lParam != IntPtr.Zero)
                        Marshal.FreeHGlobal(wParam);
                    break;
                case 18:
                    return showHiddenFiles ? new IntPtr(1) : new IntPtr(0);
                case 19:
                    FileMgrShowFiles(Marshal.PtrToStringAuto(lParam));
                    break;
                case 20:
                    {
                        new FormCheckFileUse(Marshal.PtrToStringAuto(lParam)).ShowDialog();
                        break;
                    }
            }
            return IntPtr.Zero;
        }
        private void FileMgrShowFiles(string path)
        {
            if (path == null)
            {
                path = lastShowDir;
                lastShowDir = null;
            }
            if (lastShowDir != path)
            {
                lastShowDir = path;
                listFm.Items.Clear();
                if (lastShowDir == "mycp" || lastShowDir == "\\\\")
                {
                    for (int i = 0; i < treeFmLeft.Nodes[0].Nodes.Count; i++)
                        listFm.Items.Add(new ListViewItem(treeFmLeft.Nodes[0].Nodes[i].Text, treeFmLeft.Nodes[0].Nodes[i].ImageKey) { Tag = "..\\ROOT\\" + treeFmLeft.Nodes[0].Nodes[i].Tag });
                    textBoxFmCurrent.Text = treeFmLeft.Nodes[0].Text;
                }
                else
                {
                    MFM_GetFiles(lastShowDir);
                    textBoxFmCurrent.Text = lastShowDir;
                }
                if (path == "mycp") fileSystemWatcher.Path = "";
                else fileSystemWatcher.Path = path;
                fileSystemWatcher.EnableRaisingEvents = true;


                FileMgrUpdateStatus(1);
            }
        }
        private void FileMgrUpdateStatus(int i)
        {
            FileMgrCallBack(15, new IntPtr(i), IntPtr.Zero);
        }
        private void FileMgrTreeOpenItem(TreeNode n)
        {
            if (n.Nodes.Count == 0 || n.Nodes[0].Text == str_loading && n.Tag != null)
            {
                lastClickTreeNode = n;
                string s = n.Tag.ToString();
                if (MFM_GetFolders(s))
                    lastClickTreeNode.Nodes.Remove(lastClickTreeNode.Nodes[0]);
            }
        }

        private void textBoxFmCurrent_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
                btnFmAddGoto_Click(sender, e);
        }
        private void btnFmAddGoto_Click(object sender, EventArgs e)
        {

            if (textBoxFmCurrent.Text == "")
                TaskDialog.Show(str_PleaseEnterPath, str_TipTitle);
            else
            {
                if (textBoxFmCurrent.Text.StartsWith("\"") && textBoxFmCurrent.Text.EndsWith("\""))
                {
                    textBoxFmCurrent.Text = textBoxFmCurrent.Text.Remove(textBoxFmCurrent.Text.Length - 1, 1);
                    textBoxFmCurrent.Text = textBoxFmCurrent.Text.Remove(0, 1);
                }
                if (Directory.Exists(textBoxFmCurrent.Text))
                    FileMgrShowFiles(textBoxFmCurrent.Text);
                else if (MFM_FileExist(textBoxFmCurrent.Text))
                {
                    string d = Path.GetDirectoryName(textBoxFmCurrent.Text);
                    string f = Path.GetFileName(textBoxFmCurrent.Text);
                    FileMgrShowFiles(d);
                    ListViewItem[] lis = listFm.Items.Find(f, false);
                    if (lis.Length > 0) lis[0].Selected = true;
                }
                else TaskDialog.Show(str_PathUnExists, str_TipTitle);
            }
        }
        private void treeFmLeft_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            FileMgrTreeOpenItem(e.Node);
        }
        private void treeFmLeft_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {

        }
        private void treeFmLeft_MouseClick(object sender, MouseEventArgs e)
        {
            TreeNode n = treeFmLeft.SelectedNode;
            if (n != null && n.Tag != null)
            {
                if (e.Button == MouseButtons.Left)
                    lastRightClicked = false;
                else if (e.Button == MouseButtons.Right)
                {
                    lastRightClicked = true;
                    MAppWorkShowMenuFMF(n.Tag.ToString());
                }
            }
        }
        private void treeFmLeft_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Action == TreeViewAction.ByMouse)
            {
                if (!lastRightClicked)
                {
                    lastClickTreeNode = e.Node;
                    FileMgrShowFiles(lastClickTreeNode.Tag.ToString());
                }
            }
        }
        private void treeFmLeft_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
                FileMgrTreeOpenItem(treeFmLeft.SelectedNode);
        }

        private ListViewItem currEditingItem = null;
        private void listFm_AfterLabelEdit(object sender, LabelEditEventArgs e)
        {
            if (currEditingItem != null && e.Item == 0)
            {
                string path = currEditingItem.Tag.ToString();
                string targetName = e.Label;
                //Folder
                if (path == "newfolder")
                {
                    if (targetName == "")
                    {
                        targetName = LanuageMgr.GetStr("NewFolder");
                        int ix = 1;
                        string spt = lastShowDir + "\\" + targetName + (ix == 1 ? "" : (" (" + ix + ")"));
                        bool finded = false;
                        while (!finded)
                        {
                            if (Directory.Exists(spt))
                                ix++;
                            else
                            {
                                finded = true;
                                break;
                            }
                        }
                        if (!MFM_CreateDir(spt))
                        {
                            e.CancelEdit = true;
                            listFm.Items.Remove(currEditingItem);
                            FileMgrUpdateStatus(7);
                        }
                        else FileMgrUpdateStatus(8);
                    }
                    else if (MFM_IsValidateFolderFileName(targetName))
                    {
                        string spt = lastShowDir + "\\" + targetName;
                        if (Directory.Exists(spt))
                        {
                            e.CancelEdit = true;
                            listFm.Items.Remove(currEditingItem);
                            TaskDialog.Show(str_FolderHasExist);
                        }
                        else
                        {
                            if (!MFM_CreateDir(spt))
                            {
                                e.CancelEdit = true;
                                listFm.Items.Remove(currEditingItem);
                                FileMgrUpdateStatus(7);
                            }
                            else FileMgrUpdateStatus(8);
                        }
                    }
                    else
                    {
                        e.CancelEdit = true;
                        listFm.Items.Remove(currEditingItem);
                        TaskDialog.Show(str_InvalidFileName);
                    }
                }
                else
                {

                }
            }
            else e.CancelEdit = true;
        }
        private void listFm_SelectedIndexChanged(object sender, EventArgs e)
        {
            FileMgrUpdateStatus(1);
        }
        private void listFm_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (listFm.SelectedItems.Count > 0)
                {
                    ListViewItem listViewItem = listFm.SelectedItems[0];
                    string path = listViewItem.Tag.ToString();
                    if (path.StartsWith("..\\back\\"))
                    {
                        path = path.Remove(0, 8);
                        int ix = path.LastIndexOf('\\');
                        if (ix > 0 && ix < path.Length)
                        {
                            path = path.Remove(ix);
                            FileMgrShowFiles(path);
                        }
                    }
                    else
                    {
                        if (listViewItem.ImageKey == "folder" && Directory.Exists(path))
                            FileMgrShowFiles(path);
                        else if (path.StartsWith("..\\ROOT\\"))
                        {
                            path = path.Remove(0, 8);
                            FileMgrShowFiles(path);
                        }
                        else if (MFM_FileExist(path))
                        {
                            if (path.EndsWith(".exe"))
                            {
                                if (TaskDialog.Show(str_OpenAsk, str_AskTitle, str_PathStart + path, TaskDialogButton.Yes | TaskDialogButton.No) == Result.Yes)
                                    MFM_OpenFile(path, Handle);
                            }
                            else MFM_OpenFile(path, Handle);
                        }
                    }
                }
            }
        }
        private void listFm_MouseClick(object sender, MouseEventArgs e)
        {
            if (listFm.SelectedItems.Count > 0)
            {
                ListViewItem listViewItem = listFm.SelectedItems[0];
                string path = listViewItem.Tag.ToString();
                if (e.Button == MouseButtons.Right)
                    MAppWorkShowMenuFM(path, listFm.SelectedItems.Count > 1, listFm.SelectedItems.Count);
            }
        }


        private void fileSystemWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            string fullpath = e.FullPath;
            MFM_UpdateFile(fullpath, Path.GetDirectoryName(fullpath));
        }
        private void fileSystemWatcher_Created(object sender, FileSystemEventArgs e)
        {
            string fullpath = e.FullPath;
            MFM_ReUpdateFile(fullpath, Path.GetDirectoryName(fullpath));
            FileMgrUpdateStatus(1);
        }
        private void fileSystemWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            //Remove 
            string fullpath = e.FullPath;
            ListViewItem ii = null;
            foreach (ListViewItem i in listFm.Items)
            {
                if (i.Tag.ToString() == fullpath)
                {
                    ii = i;
                    break;
                }
            }
            listFm.Items.Remove(ii);
            FileMgrUpdateStatus(1);
        }
        private void fileSystemWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            string oldfullpath = e.OldFullPath;
            string fullpath = e.FullPath;
            //Rename 
            ListViewItem ii = null;
            foreach (ListViewItem i in listFm.Items)
            {
                if (i.Tag.ToString() == oldfullpath)
                {
                    ii = i;
                    break;
                }
            }
            ii.Tag = fullpath;
            ii.Text = e.Name;
            ii.ImageKey = "*" + Path.GetExtension(fullpath);
        }

        #endregion

        #region ScMgrWork

        private class ListViewItemComparer : IComparer
        {
            private int col;
            private bool asdening = false;

            public int SortColum { get { return col; } set { col = value; } }
            public bool Asdening { get { return asdening; } set { asdening = value; } }

            public int Compare(object x, object y)
            {
                int returnVal = -1;
                if (((ListViewItem)x).SubItems[col].Text == ((ListViewItem)y).SubItems[col].Text) return -1;
                returnVal = String.Compare(((ListViewItem)x).SubItems[col].Text, ((ListViewItem)y).SubItems[col].Text);
                if (asdening) returnVal = -returnVal;
                return returnVal;
            }
        }

        private ListViewItemComparer listViewItemComparerSc = new ListViewItemComparer();
        private List<uint> scValidPid = new List<uint>();
        private List<ScItem> runningSc = new List<ScItem>();
        private Icon icoSc = null;
        private Dictionary<string, string> scGroupFriendlyName = new Dictionary<string, string>();
        private bool scCanUse = false;

        private class ScTag
        {
            public uint startType = 0;
            public uint runningState = 0;
            public string name = "";
            public string binaryPathName = "";
        }
        private class ScItem
        {
            public ScItem(int pid, string groupName, string scName, string scDsb)
            {
                this.scDsb = scDsb;
                this.scName = scName;
                this.groupName = groupName;
                this.pid = pid;
            }
            public string groupName = "";
            public string scName = "";
            public string scDsb = "";
            public int pid;
        }

        private string ScGroupNameToFriendlyName(string s)
        {
            string rs = s;
            if (LanuageMgr.IsChinese)
            {
                if (s != null)
                    if (!scGroupFriendlyName.TryGetValue(s, out rs))
                        rs = s;
            }
            return rs;
        }
        private bool ScMgrFindRunSc(PsItem p)
        {
            bool rs = false;
            if (p != null)
            {
                foreach (ScItem r in runningSc)
                {
                    if (r.pid == p.pid)
                    {
                        p.svcs.Add(r);
                        rs = true;
                    }
                }
            }
            return rs;
        }
        private string ScMgrFindDriverSc(string driverOrgPath)
        {
            string rs = "";
            foreach (ListViewItem li in listService.Items)
            {
                if (li.SubItems[7].Text == driverOrgPath)
                {
                    rs = li.Text;
                    break;
                }
            }
            return rs;
        }
        private void ScMgrInit()
        {
            if (!scListInited)
            {
                if (!MIsRunasAdmin())
                {
                    listService.Hide();
                    pl_ScNeedAdminTip.Show();
                }
                else
                {
                    scGroupFriendlyName.Add("localService", LanuageMgr.GetStr("LocalService"));
                    scGroupFriendlyName.Add("LocalService", LanuageMgr.GetStr("LocalService"));
                    scGroupFriendlyName.Add("LocalSystem", LanuageMgr.GetStr("LocalSystem"));
                    scGroupFriendlyName.Add("LocalSystemNetworkRestricted", LanuageMgr.GetStr("LocalSystemNetworkRestricted"));
                    scGroupFriendlyName.Add("LocalServiceNetworkRestricted", LanuageMgr.GetStr("LocalServiceNetworkRestricted"));
                    scGroupFriendlyName.Add("LocalServiceNoNetwork", LanuageMgr.GetStr("LocalServiceNoNetwork"));
                    scGroupFriendlyName.Add("LocalServiceAndNoImpersonation", LanuageMgr.GetStr("LocalServiceAndNoImpersonation"));
                    scGroupFriendlyName.Add("NetworkServiceAndNoImpersonation", LanuageMgr.GetStr("NetworkServiceAndNoImpersonation"));
                    scGroupFriendlyName.Add("NetworkService", LanuageMgr.GetStr("NetworkService"));
                    scGroupFriendlyName.Add("NetworkServiceNetworkRestricted", LanuageMgr.GetStr("NetworkServiceNetworkRestricted"));
                    scGroupFriendlyName.Add("UnistackSvcGroup", LanuageMgr.GetStr("UnistackSvcGroup"));
                    scGroupFriendlyName.Add("NetSvcs", LanuageMgr.GetStr("NetworkService"));
                    scGroupFriendlyName.Add("netsvcs", LanuageMgr.GetStr("NetworkService"));

                    MAppWorkCall3(182, listService.Handle, IntPtr.Zero);

                    if (!MSCM_Init())
                        TaskDialog.Show(LanuageMgr.GetStr("StartSCMFailed"), str_ErrTitle, "", TaskDialogButton.OK, TaskDialogIcon.Stop);

                    scMgrEnumServicesCallBack = ScMgrIEnumServicesCallBack;
                    scMgrEnumServicesCallBackPtr = Marshal.GetFunctionPointerForDelegate(scMgrEnumServicesCallBack);

                    scCanUse = true;
                    ScMgrRefeshList();

                }

                icoSc = new Icon(Properties.Resources.icoService, 16, 16);

                listService.ListViewItemSorter = listViewItemComparerSc;

                scListInited = true;
            }
        }
        private void ScMgrRefeshList()
        {
            if (scCanUse)
            {
                scValidPid.Clear();
                runningSc.Clear();
                listService.Items.Clear();
                MEnumServices(scMgrEnumServicesCallBackPtr);
                lbServicesCount.Text = LanuageMgr.GetStr("ServiceCount") + " : " + (listService.Items.Count == 0 ? "--" : listService.Items.Count.ToString());
            }
        }
        private void ScMgrIEnumServicesCallBack(IntPtr dspName, IntPtr scName, uint scType, uint currentState, uint dwProcessId, bool syssc,
            uint dwStartType, IntPtr lpBinaryPathName, IntPtr lpLoadOrderGroup)
        {
            ListViewItem li = new ListViewItem(Marshal.PtrToStringUni(scName));
            ScTag t = new ScTag();
            t.name = li.Text;
            t.runningState = currentState;
            t.startType = scType;
            t.binaryPathName = Marshal.PtrToStringUni(lpBinaryPathName);
            li.SubItems.Add(dwProcessId == 0 ? "" : dwProcessId.ToString());
            li.Tag = t;
            if (dwProcessId != 0)
            {
                scValidPid.Add(dwProcessId);
                runningSc.Add(new ScItem(Convert.ToInt32(dwProcessId), Marshal.PtrToStringUni(lpLoadOrderGroup), Marshal.PtrToStringUni(scName), Marshal.PtrToStringUni(dspName)));
            }
            li.SubItems.Add(Marshal.PtrToStringUni(dspName));
            switch (currentState)
            {
                case 0x0001:
                case 0x0003: li.SubItems.Add(str_status_stopped); break;
                case 0x0002:
                case 0x0004: li.SubItems.Add(str_status_running); break;
                case 0x0006:
                case 0x0007: li.SubItems.Add(str_status_paused); break;
                default: li.SubItems.Add(""); break;
            }
            li.SubItems.Add(Marshal.PtrToStringUni(lpLoadOrderGroup));
            switch (dwStartType)
            {
                case 0x0000: li.SubItems.Add(str_DriverLoad); break;
                case 0x0001: li.SubItems.Add(str_DriverLoad); break;
                case 0x0002: li.SubItems.Add(str_AutoStart); break;
                case 0x0003: li.SubItems.Add(str_DemandStart); break;
                case 0x0004: li.SubItems.Add(str_Disabled); break;
                case 0x0080: li.SubItems.Add(""); break;
                default: li.SubItems.Add(""); break;
            }
            switch (scType)
            {
                case 0x0002: li.SubItems.Add(str_FileSystem); break;
                case 0x0001: li.SubItems.Add(str_KernelDriver); break;
                case 0x0010: li.SubItems.Add(str_UserService); break;
                case 0x0020: li.SubItems.Add(str_SystemService); break;
                default: li.SubItems.Add(""); break;
            }

            string path = Marshal.PtrToStringUni(lpBinaryPathName);
            if (!MFM_FileExist(path))
            {
                StringBuilder spath = new StringBuilder(260);
                if (MCommandLineToFilePath(path, spath, 260))
                    path = spath.ToString();
            }

            bool hightlight = false;
            if (!string.IsNullOrEmpty(path) && MFM_FileExist(path))
            {
                li.SubItems.Add(path);
                StringBuilder exeCompany = new StringBuilder(256);
                if (MGetExeCompany(path, exeCompany, 256))
                {
                    li.SubItems.Add(exeCompany.ToString());
                    if (highlight_nosystem && exeCompany.ToString() != MICROSOFT)
                        hightlight = true;
                }
                else if (highlight_nosystem) hightlight = true;
            }
            else
            {
                li.SubItems.Add(path);
                li.SubItems.Add(str_FileNotExist);
                if (highlight_nosystem) hightlight = true;
            }
            if (hightlight)
            {
                li.ForeColor = Color.Blue;
                foreach (ListViewItem.ListViewSubItem s in li.SubItems)
                    s.ForeColor = Color.Blue;
            }
            listService.Items.Add(li);
        }

        private void listService_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Apps)
            {
                if (listService.SelectedItems.Count > 0)
                {
                    ListViewItem item = listService.SelectedItems[0];
                    Point p = item.Position; p.X = 0;
                    p = listService.PointToScreen(p);
                    ScTag t = item.Tag as ScTag;
                    MSCM_ShowMenu(Handle, t.name, t.runningState, t.startType, t.binaryPathName, p.X, p.Y);
                }
            }
        }
        private void listService_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (listService.SelectedItems.Count > 0)
                {
                    ListViewItem item = listService.SelectedItems[0];
                    ScTag t = item.Tag as ScTag;
                    MSCM_ShowMenu(Handle, t.name, t.runningState, t.startType, t.binaryPathName, 0, 0);
                }
            }
        }
        private void listService_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            ((ListViewItemComparer)listService.ListViewItemSorter).Asdening = !((ListViewItemComparer)listService.ListViewItemSorter).Asdening;
            ((ListViewItemComparer)listService.ListViewItemSorter).SortColum = e.Column;
            listService.Sort();
        }

        private void linkRebootAsAdmin_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            MAppRebotAdmin2("select services");
        }
        private void linkOpenScMsc_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            MFM_OpenFile("services.msc", Handle);
        }

        #endregion

        #region UWPMWork

        private void UWPListRefesh()
        {
            listUwpApps.Show();
            pl_UWPEnumFailTip.Hide();
            listUwpApps.Items.Clear();
            uwpListInited = false;
            UWPListInit();
        }
        private void UWPListInit()
        {
            if (!uwpListInited)
            {
                UWPManager uWPManager = new UWPManager();
                try
                {
                    uWPManager.EnumlateAll();
                    for (int i = 0; i < uWPManager.Packages.Count; i++)
                    {
                        TaskMgrListItem li = new TaskMgrListItem(LanuageMgr.IsChinese ? UWPManager.DisplayNameTozhCN(uWPManager.Packages[i].Name) : uWPManager.Packages[i].Name);
                        li.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                        li.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                        li.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                        li.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                        li.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                        li.SubItems[0].Font = listUwpApps.Font;
                        li.SubItems[1].Font = listUwpApps.Font;
                        li.SubItems[2].Font = listUwpApps.Font;
                        li.SubItems[3].Font = listUwpApps.Font;
                        li.SubItems[4].Font = listUwpApps.Font;
                        li.SubItems[0].Text = LanuageMgr.IsChinese ? UWPManager.DisplayNameTozhCN(uWPManager.Packages[i].Name) : uWPManager.Packages[i].Name;
                        li.SubItems[1].Text = uWPManager.Packages[i].Publisher;
                        li.SubItems[2].Text = uWPManager.Packages[i].Description;
                        li.SubItems[3].Text = uWPManager.Packages[i].FullName;
                        li.SubItems[4].Text = uWPManager.Packages[i].InstalledLocation;
                        li.Tag = uWPManager.Packages[i];
                        li.IsUWPICO = true;

                        string iconpath = uWPManager.Packages[i].IconPath;
                        if (iconpath != "" && MFM_FileExist(iconpath))
                        {
                            using (Image img = Image.FromFile(iconpath))
                                li.Icon = IconUtils.ConvertToIcon(img);
                            //
                            //     li.Image = IconUtils.GetThumbnail(new Bitmap(iconpath), 16, 16);
                        }
                        listUwpApps.Items.Add(li);
                    }
                }
                catch (Exception e)
                {
                    listUwpApps.Hide();
                    pl_UWPEnumFailTip.Show();
                    lbUWPEnumFailText.Text = LanuageMgr.GetStr("UWPEnumFail") + "\n\n" + e.ToString();
                }
                uwpListInited = true;
            }
        }
        private TaskMgrListItem UWPListFindItem(string fullName)
        {
            TaskMgrListItem rs = null;
            foreach (TaskMgrListItem r in listUwpApps.Items)
                if (r.Tag.ToString() == fullName)
                {
                    rs = r;
                    break;
                }
            return rs;
        }

        private void 打开应用ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listUwpApps.SelectedItem != null)
            {
                //explorer shell:AppsFolder\
                //explorer shell:AppsFolder\1F8B0F94.122165AE053F_1.4.1.0_x64__j2p0p5q0044a6!App
                //explorer shell:AppsFolder\c5e2524a-ea46-4f67-841f-6a9465d9d515_cw5n1h2txyewy!App
                //1F8B0F94.122165AE053F_1.4.1.0_x64__j2p0p5q0044a6
                UWPPackage pkg = ((UWPPackage)listUwpApps.SelectedItem.Tag);
                if (pkg != null & pkg.Apps != null && pkg.Apps.Length > 0)
                {
                    string pkhname = listUwpApps.SelectedItem.Tag.ToString();
                    if (pkhname.Contains("_") && pkhname.LastIndexOf('_') != 0)
                    {
                        int startindex = pkhname.IndexOf('_');
                        int endindex = pkhname.LastIndexOf('_');
                        if (endindex > startindex && endindex - startindex > 0)
                        {
                            string pkhnamez = pkhname.Substring(startindex, endindex - startindex);
                            pkhname = pkhname.Replace(pkhnamez, "");
                        }
                    }
                    MRunUWPApp(pkhname, pkg.Apps[0]);
                }
            }
        }
        private void 卸载应用ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listUwpApps.SelectedItem != null)
            {
                MUnInstallUWPApp(((UWPPackage)listUwpApps.SelectedItem.Tag).ToString());
            }
        }
        private void 打开安装位置ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listUwpApps.SelectedItem != null)
            {
                MFM_OpenFile(listUwpApps.SelectedItem.SubItems[4].Text, Handle);
            }
        }
        private void 复制名称ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listUwpApps.SelectedItem != null)
                MCopyToClipboard2(listUwpApps.SelectedItem.SubItems[0].Text);
        }
        private void 复制完整名称ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listUwpApps.SelectedItem != null)
                MCopyToClipboard2(listUwpApps.SelectedItem.SubItems[3].Text);
        }

        private void listUwpApps_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Apps)
            {
                if (listUwpApps.SelectedItem != null)
                {
                    Point p = listUwpApps.GetiItemPoint(listUwpApps.SelectedItem);
                    contextMenuStripUWP.Show(listUwpApps.PointToScreen(p));
                }
            }
        }
        private void listUwpApps_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && listUwpApps.SelectedItem != null)
                contextMenuStripUWP.Show(MousePosition);
        }

        #endregion

        #region PerfWork

        public static Color CpuDrawColor = Color.FromArgb(17, 125, 187);
        public static Color CpuBgColor = Color.FromArgb(241, 246, 250);
        public static Color RamDrawColor = Color.FromArgb(139, 18, 174);
        public static Color RamBgColor = Color.FromArgb(244, 242, 244);
        public static Color DiskDrawColor = Color.FromArgb(77, 166, 12);
        public static Color DiskBgColor = Color.FromArgb(239, 247, 223);
        public static Color NetDrawColor = Color.FromArgb(167, 79, 1);
        public static Color NetBgColor = Color.FromArgb(252, 243, 235);

        PerformanceListItem perf_cpu = new PerformanceListItem();
        PerformanceListItem perf_ram = new PerformanceListItem();

        private class PerfItemHeader
        {
            public IntPtr performanceCounterNative = IntPtr.Zero;
            public PerformanceListItem item = null;
            public IPerformancePage performancePage = null;

            public override string ToString()
            {
                if (item != null)
                    return item.ToString();
                return base.ToString();
            }
        }

        private List<PerfItemHeader> perfItems = new List<PerfItemHeader>();
        private List<IPerformancePage> perfPages = new List<IPerformancePage>();

        private IPerformancePage currSelectedPerformancePage = null;

        private void performanceLeftList_SelectedtndexChanged(object sender, EventArgs e)
        {
            if (performanceLeftList.Selectedtem == perf_cpu)
                PerfPagesTo(0);
            else if (performanceLeftList.Selectedtem == perf_ram)
                PerfPagesTo(1);
            else if (performanceLeftList.Selectedtem.PageIndex != 0)
                PerfPagesTo(performanceLeftList.Selectedtem.PageIndex);
            else
                PerfPagesToNull();
        }
        private void PerfPagesToNull()
        {
            if (currSelectedPerformancePage != null)
                currSelectedPerformancePage.PageHide();
            currSelectedPerformancePage = null;
        }
        private void PerfPagesTo(int index)
        {
            if (currSelectedPerformancePage != null)
                currSelectedPerformancePage.PageHide();
            currSelectedPerformancePage = null;
            currSelectedPerformancePage = perfPages[index];
            currSelectedPerformancePage.PageShow();
        }
        private void PerfPagesAddToCtl(Control c)
        {
            c.Visible = false;
            splitContainerPerfCtls.Panel2.Controls.Add(c);
            c.Size = new Size(splitContainerPerfCtls.Panel2.Width - 30, splitContainerPerfCtls.Panel2.Height - 30);
            c.Location = new Point(15, 15);
            c.Anchor = AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right | AnchorStyles.Top;
        }
        private void PerfPagesInit()
        {
            PerformancePageCpu performanceCpu = new PerformancePageCpu();
            PerfPagesAddToCtl(performanceCpu);
            perfPages.Add(performanceCpu);

            PerformancePageRam performanceRam = new PerformancePageRam();
            PerfPagesAddToCtl(performanceRam);
            perfPages.Add(performanceRam);
        }
        private void PerfInit()
        {
            if (!perfInited)
            {
                MDEVICE_Init();

                perf_cpu.Name = "CPU";
                perf_cpu.SmallText = "0 %";
                perf_cpu.BasePen = new Pen(CpuDrawColor, 2);
                perf_cpu.BgBrush = new SolidBrush(CpuBgColor);
                performanceLeftList.Items.Add(perf_cpu);

                perf_ram.Name = LanuageMgr.GetStr("TitleRam");
                perf_ram.SmallText = "0 %";
                perf_ram.BasePen = new Pen(RamDrawColor, 2);
                perf_ram.BgBrush = new SolidBrush(RamBgColor);
                performanceLeftList.Items.Add(perf_ram);

                PerfPagesInit();

                MDEVICE_GetLogicalDiskInfo();
                uint count = MPERF_InitDisksPerformanceCounters();
                for (int i = 0; i < count; i++)
                {
                    PerfItemHeader perfItemHeader = new PerfItemHeader();
                    perfItemHeader.performanceCounterNative = MPERF_GetDisksPerformanceCounters(i);
                    perfItemHeader.item = new PerformanceListItem();

                    StringBuilder sb = new StringBuilder(32);
                    MPERF_GetDisksPerformanceCountersInstanceName(perfItemHeader.performanceCounterNative, sb, 32);
                    uint diskIndex = (uint)(count - i -1);// MDEVICE_GetPhysicalDriveFromPartitionLetter(sb.ToString()[2]);

                    perfItemHeader.item.Name = LanuageMgr.GetStr("TitleDisk") + sb.ToString();
                    perfItemHeader.item.BasePen = new Pen(DiskDrawColor);
                    perfItemHeader.item.BgBrush = new SolidBrush(DiskBgColor);
                    perfItems.Add(perfItemHeader);

                    PerformancePageDisk performancedisk = new PerformancePageDisk(perfItemHeader.performanceCounterNative, diskIndex);
                    PerfPagesAddToCtl(performancedisk);
                    perfPages.Add(performancedisk);

                    perfItemHeader.performancePage = performancedisk;

                    perfItemHeader.item.PageIndex = perfPages.Count - 1;
                    performanceLeftList.Items.Add(perfItemHeader.item);
                }

                count = MPERF_InitNetworksPerformanceCounters();
                for (int i = 0; i < count; i++)
                {
                    PerfItemHeader perfItemHeader = new PerfItemHeader();
                    perfItemHeader.performanceCounterNative = MPERF_GetNetworksPerformanceCounters(i);
                    perfItemHeader.item = new PerformanceListItem();
                    perfItemHeader.item.Name = LanuageMgr.GetStr("TitleNet");
                    perfItemHeader.item.BasePen = new Pen(NetDrawColor);
                    perfItemHeader.item.BgBrush = new SolidBrush(NetBgColor);
                    perfItems.Add(perfItemHeader);

                    PerformancePageNet performancenet = new PerformancePageNet(perfItemHeader.performanceCounterNative);
                    PerfPagesAddToCtl(performancenet);
                    perfPages.Add(performancenet);

                    perfItemHeader.performancePage = performancenet;

                    perfItemHeader.item.PageIndex = perfPages.Count - 1;
                    performanceLeftList.Items.Add(perfItemHeader.item);
                }

                performanceLeftList.UpdateAll();
                performanceLeftList.Invalidate();

                PerfPagesTo(0);



                perfInited = true;
            }
        }
        private void PerfUpdate()
        {
            foreach (PerfItemHeader h in perfItems)
            {
                double data = h.performancePage.PageUpdateSimple();
                h.performancePage.PageFroceSetData((int)data);
                h.item.SmallText = data.ToString("0.0") + "%";
                h.item.AddData((int)data);
            }

            if (currSelectedPerformancePage != null)
                currSelectedPerformancePage.PageUpdate();
        }
        private void PerfClear()
        {
            foreach (IPerformancePage h in perfPages)
                h.PageDelete();
            perfPages.Clear();

            MPERF_DestroyDisksPerformanceCounters();
            perfItems.Clear();

            MDEVICE_DestroyLogicalDiskInfo();
            MDEVICE_UnInit();
        }
        private void PerfUpdateGridUnit()
        {
            string unistr = "";
            if (baseProcessRefeshTimer.Interval != 0)
                unistr = (baseProcessRefeshTimer.Interval / 1000 * 60).ToString() + str_sec;
            else unistr = str_status_paused;
            foreach (IPerformancePage p in perfPages)
                p.PageSetGridUnit(unistr);
        }

        private void linkLabelOpenPerfMon_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            MRunExe("perfmon.exe", "/res");
        }

        #endregion

        #region LanuageWork

        public static string str_idle_process = "";
        public static string str_service_host = "";
        public static string str_status_paused = "";
        public static string str_status_hung = "";
        public static string str_status_running = "";
        public static string str_status_stopped = "";

        public static string str_proc_count = "";
        public static string str_proc_32 = "";
        public static string str_get_failed = "";
        public static string str_sec = "";
        public static string str_loading = "";
        public static string str_frocedelsuccess = "";
        public static string str_filldatasuccess = "";
        public static string str_filldatafailed = "";
        public static string str_getfileinfofailed = "";
        public static string str_filenotexist = "";
        public static string str_failed = "";

        public static string str_endproc = "";
        public static string str_endtask = "";
        public static string str_resrat = "";

        public static string str_VisitFolderFailed = "";
        public static string str_TipTitle = "";
        public static string str_ErrTitle = "";
        public static string str_AskTitle = "";
        public static string str_PathUnExists = "";
        public static string str_PleaseEnterPath = "";
        public static string str_Ready = "";
        public static string str_ReadyStatus = "";
        public static string str_ReadyStatusEnd = "";
        public static string str_ReadyStatusEnd2 = "";
        public static string str_FileCuted = "";
        public static string str_FileCopyed = "";
        public static string str_NewFolderFailed = "";
        public static string str_NewFolderSuccess = "";
        public static string str_PathCopyed = "";
        public static string str_FolderCuted = "";
        public static string str_FolderCopyed = "";
        public static string str_FolderHasExist = "";
        public static string str_OpenAsk = "";
        public static string str_PathStart = "";
        public static string str_DriverLoad = "";
        public static string str_AutoStart = "";
        public static string str_DemandStart = "";
        public static string str_Disabled = "";
        public static string str_FileSystem = "";
        public static string str_KernelDriver = "";
        public static string str_UserService = "";
        public static string str_SystemService = "";
        public static string str_InvalidFileName = "";
        public static string str_RefeshSuccess = "";
        public static string str_InvalidHwnd = "";
        public static string str_ChangeWindowTextAsk = "";
        public static string str_UnlockFileSuccess = "";
        public static string str_UnlockFileFailed = "";
        public static string str_CollectingFiles = "";
        public static string str_DeleteFiles = "";
        public static string str_PleaseChooseDriver = "";
        public static string str_DriverLoadSuccessFull = "";
        public static string str_DriverLoadFailed = "";
        public static string str_DriverUnLoadSuccessFull = "";
        public static string str_DriverUnLoadFailed = "";
        public static string str_PleaseEnterDriverServiceName = "";
        public static string str_DriverCount = "";
        public static string str_FileNotExist = "";
        public static string str_DriverCountLoaded = "";
        public static string str_AppTitle = "";
        public static string str_FileTrust = "";
        public static string str_FileTrustViewCrt = "";
        public static string str_FunCreateing = "";
        public static string str_PleaseEnterTargetAddress = "";
        public static string str_PleaseEnterDaSize = "";
        public static string str_DblClickToDa = "";
        public static string str_KillAskStart = "";
        public static string str_KillAskEnd = "";
        public static string str_KillAskImporantGiveup = "";
        public static string str_KillAskContentImporant = "";
        public static string str_Close = "";
        public static string str_Cancel = "";
        public static string str_KillAskContentVeryImporant = "";
        public static string str_TitleVeryWarn = "";
        public static string str_SuspendStart = "";
        public static string str_SuspendEnd = "";
        public static string str_SuspendWarnContent = "";
        public static string str_SuspendVeryImporantWarnContent = "";
        public static string str_DblCklShow_EPROCESS = "";
        public static string str_DblCklShow_KPROCESS = "";
        public static string str_DblCklShow_PEB = "";
        public static string str_DblCklShow_RTL_USER_PROCESS_PARAMETERS = "";
        public static string str_CantFind = "";
        public static string str_No = "";
        public static string str_Yes = "";

        /*
        
         * DblCklShow_EPROCESS	Double Click this item to show _EPROCESS of process	
DblCklShow_KPROCESS	Double Click this item to show _KPROCESS of process	
DblCklShow_PEB	Double Click this item to show _PEB of process	
DblCklShow_RTL_USER_PROCESS_PARAMETERS	Double Click this item to show RTL_USER_PROCESS_PARAMETERS  of process	

         */

        public static void InitLanuage()
        {
            string lanuage = GetConfig("Lanuage", "AppSetting");
            if (lanuage != "")
            {
                try
                {
                    Log("Load Lanuage Resource : " + lanuage);
                    LanuageMgr.LoadLanuageResource(lanuage);
                }
                catch (Exception e)
                {
                    LogErr("Lanuage resource load failed !\n" + e.ToString());
                    MessageBox.Show(e.ToString(), "ERROR !", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                LanuageMgr.LoadLanuageResource("zh");
                SetConfig("Lanuage", "AppSetting", "zh");
                LogWarn("Not found Lanuage settings , use default zh-CN .");
            }

            InitLanuageItems();
            if (lanuage != "" && lanuage != "zh" && lanuage != "zh-CN") System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(lanuage);

            MLG_SetLanuageRes(null, lanuage);
        }
        private static void InitLanuageItems()
        {
            try
            {
                MLG_SetLanuageItems_CanRealloc();

                str_No = LanuageMgr.GetStr("No");
                str_Yes = LanuageMgr.GetStr("Yes");
                str_DblClickToDa = LanuageMgr.GetStr("DblClickToDa");
                str_FunCreateing = LanuageMgr.GetStr("FunCreateing");
                str_FileTrustViewCrt = LanuageMgr.GetStr("FileTrustViewCrt");
                str_AppTitle = LanuageMgr.GetStr("AppTitle");
                str_DriverCountLoaded = LanuageMgr.GetStr("DriverCountLoaded");
                str_FileNotExist = LanuageMgr.GetStr("FileNotExist");
                str_PleaseEnterDriverServiceName = LanuageMgr.GetStr("PleaseEnterDriverServiceName");
                str_DriverUnLoadFailed = LanuageMgr.GetStr("DriverUnLoadFailed");
                str_DriverUnLoadSuccessFull = LanuageMgr.GetStr("DriverUnLoadSuccessFull");
                str_DriverLoadSuccessFull = LanuageMgr.GetStr("DriverLoadSuccessFull");
                str_DriverLoadSuccessFull = LanuageMgr.GetStr("DriverLoadSuccessFull");
                str_DeleteFiles = LanuageMgr.GetStr("DeleteFiles");
                str_CollectingFiles = LanuageMgr.GetStr("CollectingFiles");
                str_UnlockFileSuccess = LanuageMgr.GetStr("UnlockFileSuccess");
                str_UnlockFileFailed = LanuageMgr.GetStr("UnlockFileFailed");
                str_filenotexist = LanuageMgr.GetStr("PathUnExists");
                str_failed = LanuageMgr.GetStr("OpFailed");
                str_getfileinfofailed = LanuageMgr.GetStr("GetFileInfoFailed");
                str_filldatasuccess = LanuageMgr.GetStr("FillFileSuccess");
                str_filldatafailed = LanuageMgr.GetStr("FillFileFailed");
                str_frocedelsuccess = LanuageMgr.GetStr("FroceDelSuccess");
                str_idle_process = LanuageMgr.GetStr("SystemIdleProcess");
                str_service_host = LanuageMgr.GetStr("ServiceHost");
                str_status_paused = LanuageMgr.GetStr("StatusPaused");
                str_status_hung = LanuageMgr.GetStr("StatusHang");
                str_proc_count = LanuageMgr.GetStr("ProcessCount");
                str_proc_32 = LanuageMgr.GetStr("Process32Bit");
                str_get_failed = LanuageMgr.GetStr("GetFailed");
                str_sec = LanuageMgr.GetStr("Second");
                str_status_running = LanuageMgr.GetStr("StatusRunning");
                str_status_stopped = LanuageMgr.GetStr("StatusStopped");
                str_endproc = LanuageMgr.GetStr("BtnEndProcess");
                str_endtask = LanuageMgr.GetStr("BtnEndTask");
                str_resrat = LanuageMgr.GetStr("BtnRestartText");
                str_loading = LanuageMgr.GetStr("Loading");
                str_VisitFolderFailed = LanuageMgr.GetStr("VisitFolderFailed");
                str_TipTitle = LanuageMgr.GetStr("TipTitle");
                str_ErrTitle = LanuageMgr.GetStr("ErrTitle");
                str_AskTitle = LanuageMgr.GetStr("AskTitle ");
                str_PathUnExists = LanuageMgr.GetStr("PathUnExists");
                str_PleaseEnterPath = LanuageMgr.GetStr("PleaseEnterPath");
                str_Ready = LanuageMgr.GetStr("Ready");
                str_ReadyStatus = LanuageMgr.GetStr("ReadyStatus");
                str_ReadyStatusEnd = LanuageMgr.GetStr("ReadyStatusEnd");
                str_ReadyStatusEnd2 = LanuageMgr.GetStr("ReadyStatusEnd2");
                str_FileCuted = LanuageMgr.GetStr("FileCuted");
                str_FileCopyed = LanuageMgr.GetStr("FileCopyed");
                str_NewFolderFailed = LanuageMgr.GetStr("NewFolderFailed");
                str_NewFolderSuccess = LanuageMgr.GetStr("NewFolderSuccess ");
                str_PathCopyed = LanuageMgr.GetStr("PathCopyed");
                str_FolderCuted = LanuageMgr.GetStr("FolderCuted");
                str_FolderCopyed = LanuageMgr.GetStr("FolderCopyed");
                str_FolderHasExist = LanuageMgr.GetStr("FolderHasExist");
                str_OpenAsk = LanuageMgr.GetStr("OpenAsk");
                str_PathStart = LanuageMgr.GetStr("PathStart");
                str_DriverLoad = LanuageMgr.GetStr("DriverLoad");
                str_AutoStart = LanuageMgr.GetStr("AutoStart ");
                str_DemandStart = LanuageMgr.GetStr("DemandStart");
                str_Disabled = LanuageMgr.GetStr("Disabled");
                str_FileSystem = LanuageMgr.GetStr("FileSystem");
                str_KernelDriver = LanuageMgr.GetStr("KernelDriver");
                str_UserService = LanuageMgr.GetStr("UserService");
                str_SystemService = LanuageMgr.GetStr("SystemService");
                str_InvalidFileName = LanuageMgr.GetStr("InvalidFileName");
                str_InvalidHwnd = LanuageMgr.GetStr("InvalidHwnd");
                str_RefeshSuccess = LanuageMgr.GetStr("RefeshSuccess");
                str_PleaseChooseDriver = LanuageMgr.GetStr("PleaseChooseDriver");
                str_DriverCount = LanuageMgr.GetStr("DriverCount");
                str_FileTrust = LanuageMgr.GetStr("FileTrust");
                str_PleaseEnterTargetAddress = LanuageMgr.GetStr("PleaseEnterTargetAddress");
                str_PleaseEnterDaSize = LanuageMgr.GetStr("PleaseEnterDaSize");
                str_KillAskStart = LanuageMgr.GetStr("KillAskStart");
                str_KillAskEnd = LanuageMgr.GetStr("KillAskEnd");
                str_KillAskImporantGiveup = LanuageMgr.GetStr("KillAskImporantGiveup");
                str_KillAskContentImporant = LanuageMgr.GetStr("KillAskContentImporant");
                str_Close = LanuageMgr.GetStr("Close");
                str_Cancel = LanuageMgr.GetStr("Cancel");
                str_KillAskContentVeryImporant = LanuageMgr.GetStr("KillAskContentVeryImporant");
                str_TitleVeryWarn = LanuageMgr.GetStr("TitleVeryWarn");
                str_SuspendStart = LanuageMgr.GetStr("SuspendStart");
                str_SuspendEnd = LanuageMgr.GetStr("SuspendEnd");
                str_SuspendWarnContent = LanuageMgr.GetStr("SuspendWarnContent");
                str_SuspendVeryImporantWarnContent = LanuageMgr.GetStr("SuspendVeryImporantWarnContent");
                str_DblCklShow_EPROCESS = LanuageMgr.GetStr("DblCklShow_EPROCESS");
                str_DblCklShow_KPROCESS = LanuageMgr.GetStr("DblCklShow_KPROCESS");
                str_DblCklShow_PEB = LanuageMgr.GetStr("DblCklShow_PEB");
                str_DblCklShow_RTL_USER_PROCESS_PARAMETERS = LanuageMgr.GetStr("DblCklShow_RTL_USER_PROCESS_PARAMETERS");
                str_CantFind = LanuageMgr.GetStr("CantFind");

                MAppSetLanuageItems(0, 0, str_KillAskStart, 0);
                MAppSetLanuageItems(0, 1, str_KillAskEnd, 0);
                MAppSetLanuageItems(0, 2, LanuageMgr.GetStr("KillAskContent"), 0);
                MAppSetLanuageItems(0, 3, LanuageMgr.GetStr("KillFailed"), 0);
                MAppSetLanuageItems(0, 4, LanuageMgr.GetStr("AccessDenied"), 0);
                MAppSetLanuageItems(0, 5, LanuageMgr.GetStr("OpFailed"), 0);
                MAppSetLanuageItems(0, 6, LanuageMgr.GetStr("InvalidProcess"), 0);
                MAppSetLanuageItems(0, 7, LanuageMgr.GetStr("CantCopyFile"), 0);
                MAppSetLanuageItems(0, 8, LanuageMgr.GetStr("CantMoveFile"), 0);
                MAppSetLanuageItems(0, 9, LanuageMgr.GetStr("ChooseTargetDir"), 0);

                int size = 0;
                MAppSetLanuageItems(1, 0, LanuageMgr.GetStr2("Moveing", out size), size);
                MAppSetLanuageItems(1, 1, LanuageMgr.GetStr2("Copying", out size), size);
                MAppSetLanuageItems(1, 2, LanuageMgr.GetStr2("FileExist", out size), size);
                MAppSetLanuageItems(1, 3, LanuageMgr.GetStr2("FileExist2", out size), size);
                MAppSetLanuageItems(1, 4, LanuageMgr.GetStr2("TitleQuestion", out size), size);
                MAppSetLanuageItems(1, 5, LanuageMgr.GetStr2("TipTitle", out size), size);
                MAppSetLanuageItems(1, 6, LanuageMgr.GetStr2("DelSure", out size), size);
                MAppSetLanuageItems(1, 7, LanuageMgr.GetStr2("DelAsk1", out size), size);
                MAppSetLanuageItems(1, 8, LanuageMgr.GetStr2("DelAsk2", out size), size);
                MAppSetLanuageItems(1, 9, LanuageMgr.GetStr2("DelAsk3", out size), size);
                MAppSetLanuageItems(1, 10, LanuageMgr.GetStr2("DeleteIng", out size), size);
                MAppSetLanuageItems(1, 11, LanuageMgr.GetStr2("NoAdminTipText", out size), size);
                MAppSetLanuageItems(1, 12, LanuageMgr.GetStr2("NoAdminTipTitle", out size), size);
                MAppSetLanuageItems(1, 13, LanuageMgr.GetStr2("DelFailed", out size), size);
                MAppSetLanuageItems(1, 14, str_idle_process, str_idle_process.Length + 1);
                MAppSetLanuageItems(1, 15, LanuageMgr.GetStr2("EndProcFailed", out size), size);
                MAppSetLanuageItems(1, 16, LanuageMgr.GetStr2("OpenProcFailed", out size), size);
                MAppSetLanuageItems(1, 17, LanuageMgr.GetStr2("SusProcFailed", out size), size);
                MAppSetLanuageItems(1, 18, LanuageMgr.GetStr2("ResProcFailed", out size), size);
                MAppSetLanuageItems(1, 19, LanuageMgr.GetStr2("MenuRebootAsAdmin", out size), size);
                MAppSetLanuageItems(1, 20, LanuageMgr.GetStr2("Visible", out size), size);
                MAppSetLanuageItems(1, 21, LanuageMgr.GetStr2("CantGetPath", out size), size);
                MAppSetLanuageItems(1, 22, LanuageMgr.GetStr2("FreeLibSuccess", out size), size);
                MAppSetLanuageItems(1, 23, LanuageMgr.GetStr2("Priority", out size), size);
                MAppSetLanuageItems(1, 24, LanuageMgr.GetStr2("EntryPoint", out size), size);
                MAppSetLanuageItems(1, 25, LanuageMgr.GetStr2("ModuleName", out size), size);
                MAppSetLanuageItems(1, 26, LanuageMgr.GetStr2("State", out size), size);
                MAppSetLanuageItems(1, 27, LanuageMgr.GetStr2("ContextSwitch", out size), size);
                MAppSetLanuageItems(1, 28, LanuageMgr.GetStr2("ModulePath", out size), size);
                MAppSetLanuageItems(1, 29, LanuageMgr.GetStr2("Address", out size), size);
                MAppSetLanuageItems(1, 30, LanuageMgr.GetStr2("Size", out size), size);
                MAppSetLanuageItems(1, 31, LanuageMgr.GetStr2("TitlePublisher", out size), size);
                MAppSetLanuageItems(1, 32, LanuageMgr.GetStr2("WindowText", out size), size);
                MAppSetLanuageItems(1, 33, LanuageMgr.GetStr2("WindowHandle", out size), size);
                MAppSetLanuageItems(1, 34, LanuageMgr.GetStr2("WindowClassName", out size), size);
                MAppSetLanuageItems(1, 35, LanuageMgr.GetStr2("BelongThread", out size), size);
                MAppSetLanuageItems(1, 36, LanuageMgr.GetStr2("VWinTitle", out size), size);
                MAppSetLanuageItems(1, 37, LanuageMgr.GetStr2("VModulTitle", out size), size);
                MAppSetLanuageItems(1, 38, LanuageMgr.GetStr2("VThreadTitle", out size), size);
                MAppSetLanuageItems(1, 39, LanuageMgr.GetStr2("EnumModuleFailed", out size), size);
                MAppSetLanuageItems(1, 40, LanuageMgr.GetStr2("EnumThreadFailed", out size), size);
                MAppSetLanuageItems(1, 41, LanuageMgr.GetStr2("FreeInvalidProc", out size), size);
                MAppSetLanuageItems(1, 42, LanuageMgr.GetStr2("FreeFailed", out size), size);
                MAppSetLanuageItems(1, 43, LanuageMgr.GetStr2("KillThreadError", out size), size);
                MAppSetLanuageItems(1, 44, LanuageMgr.GetStr2("KillThreadInvThread", out size), size);
                MAppSetLanuageItems(1, 45, LanuageMgr.GetStr2("OpenThreadFailed", out size), size);
                MAppSetLanuageItems(1, 46, LanuageMgr.GetStr2("SuThreadErr", out size), size);
                MAppSetLanuageItems(1, 47, LanuageMgr.GetStr2("ReThreadErr", out size), size);
                MAppSetLanuageItems(1, 48, LanuageMgr.GetStr2("InvThread", out size), size);
                MAppSetLanuageItems(1, 49, LanuageMgr.GetStr2("SuThreadWarn", out size), size);
                MAppSetLanuageItems(1, 50, LanuageMgr.GetStr2("KernelNotLoad", out size), size);

                MAppSetLanuageItems(2, 0, LanuageMgr.GetStr2("DelStartupItemAsk", out size), size);
                MAppSetLanuageItems(2, 1, LanuageMgr.GetStr2("DelStartupItemAsk2", out size), size);
                MAppSetLanuageItems(2, 2, str_endtask, str_endtask.Length + 1);
                MAppSetLanuageItems(2, 3, str_resrat, str_resrat.Length + 1);
                MAppSetLanuageItems(2, 4, LanuageMgr.GetStr2("LoadDriver", out size), size);
                MAppSetLanuageItems(2, 5, LanuageMgr.GetStr2("UnLoadDriver", out size), size);
                MAppSetLanuageItems(2, 6, str_FileNotExist, str_FileNotExist.Length + 1);
                MAppSetLanuageItems(2, 7, LanuageMgr.GetStr2("FileTrust", out size), size);
                MAppSetLanuageItems(2, 8, LanuageMgr.GetStr2("FileNotTrust", out size), size);
                MAppSetLanuageItems(2, 9, LanuageMgr.GetStr2("OpenServiceError", out size), size);
                MAppSetLanuageItems(2, 10, LanuageMgr.GetStr2("DelScError", out size), size);
                MAppSetLanuageItems(2, 11, LanuageMgr.GetStr2("ChangeScStartTypeFailed", out size), size);



            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString(), "ERROR !", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private string Native_LanuageItems_CallBack(string s)
        {
            return LanuageMgr.GetStr(s);
        }

        #endregion

        #region StartMWork

        private TaskMgrListItemGroup knowDlls = null;
        private TaskMgrListItemGroup rightMenu1 = null;
        private TaskMgrListItemGroup rightMenu2 = null;
        private TaskMgrListItemGroup rightMenu3 = null;
        private TaskMgrListItemGroup printMonitors = null;
        private TaskMgrListItemGroup printProviders = null;

        private static uint startId = 0;

        private struct startitem
        {
            public uint id;
            public startitem(string s, IntPtr rootregpath, string path, string valuename)
            {
                this.filepath = s; this.rootregpath = rootregpath;
                this.path = path;
                this.valuename = valuename;
                id = startId++;
            }
            public string valuename;
            public string path;
            public string filepath;
            public IntPtr rootregpath;
        }
        private void StartMListInit()
        {
            if (!startListInited)
            {
                enumStartupsCallBack = StartMList_CallBack;
                enumStartupsCallBackPtr = Marshal.GetFunctionPointerForDelegate(enumStartupsCallBack);
                knowDlls = new TaskMgrListItemGroup("Know Dlls");
                knowDlls.Text = "Know Dlls";
                knowDlls.Icon = Properties.Resources.icoFiles;
                knowDlls.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                knowDlls.Type = TaskMgrListItemType.ItemGroup;
                knowDlls.DisplayChildCount = true;
                rightMenu1 = new TaskMgrListItemGroup("RightMenu 1");
                rightMenu1.Text = "RightMenu 1";
                rightMenu1.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                rightMenu1.Type = TaskMgrListItemType.ItemGroup;
                rightMenu1.DisplayChildCount = true;
                rightMenu1.Image = Properties.Resources.iconContextMenu;
                rightMenu2 = new TaskMgrListItemGroup("RightMenu 2");
                rightMenu2.Text = "RightMenu 2";
                rightMenu2.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                rightMenu2.Type = TaskMgrListItemType.ItemGroup;
                rightMenu2.DisplayChildCount = true;
                rightMenu2.Image = Properties.Resources.iconContextMenu;
                rightMenu3 = new TaskMgrListItemGroup("RightMenu 3");
                rightMenu3.Text = "RightMenu 3";
                rightMenu3.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                rightMenu3.Type = TaskMgrListItemType.ItemGroup;
                rightMenu3.DisplayChildCount = true;
                rightMenu3.Image = Properties.Resources.iconContextMenu;

                printMonitors = new TaskMgrListItemGroup("PrintMonitors");
                printMonitors.Text = "PrintMonitors";
                printMonitors.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                printMonitors.Type = TaskMgrListItemType.ItemGroup;
                printMonitors.DisplayChildCount = true;
                printMonitors.Icon = Properties.Resources.icoWins;

                printProviders = new TaskMgrListItemGroup("PrintProviders");
                printProviders.Text = "PrintProviders";
                printProviders.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem());
                printProviders.Type = TaskMgrListItemType.ItemGroup;
                printProviders.DisplayChildCount = true;
                printProviders.Icon = Properties.Resources.icoWins;

                StartMListRefesh();
                startListInited = true;
            }
        }
        private void StartMListRefesh()
        {
            knowDlls.Childs.Clear();
            rightMenu2.Childs.Clear();
            rightMenu1.Childs.Clear();
            listStartup.Items.Clear();
            startId = 0;

            listStartup.Items.Add(knowDlls);
            listStartup.Items.Add(rightMenu1);
            listStartup.Items.Add(rightMenu2);
            listStartup.Items.Add(rightMenu3);
            listStartup.Items.Add(printMonitors);
            listStartup.Items.Add(printProviders);
            MEnumStartups(enumStartupsCallBackPtr);
        }
        private void StartMList_CallBack(IntPtr name, IntPtr type, IntPtr path, IntPtr rootregpath, IntPtr regpath, IntPtr regvalue)
        {
            bool settoblue = false;
            TaskMgrListItem li = new TaskMgrListItem(Marshal.PtrToStringUni(name));
            for (int i = 0; i < 5; i++) li.SubItems.Add(new TaskMgrListItem.TaskMgrListViewSubItem() { Font = listStartup.Font });
            li.IsFullData = true;
            li.SubItems[0].Text = li.Text;
            // li.SubItems[1].Text = Marshal.PtrToStringUni(type);
            li.Type = TaskMgrListItemType.ItemMain;
            StringBuilder filePath = null;
            if (path != IntPtr.Zero)
            {
                string pathstr = Marshal.PtrToStringUni(path);
                if (!pathstr.StartsWith("\"")) { pathstr = "\"" + pathstr + "\""; }
                li.SubItems[1].Text = (pathstr);
                filePath = new StringBuilder(260);
                if (MCommandLineToFilePath(pathstr, filePath, 260))
                {
                    li.SubItems[2].Text = filePath.ToString();
                    pathstr = filePath.ToString();
                    if (MFM_FileExist(pathstr))
                    {
                        li.Icon = Icon.FromHandle(MGetExeIcon(pathstr));
                        StringBuilder exeCompany = new StringBuilder(256);
                        if (MGetExeCompany(pathstr, exeCompany, 256))
                        {
                            li.SubItems[3].Text = exeCompany.ToString();
                            if (highlight_nosystem && li.SubItems[3].Text != MICROSOFT)
                                settoblue = true;
                        }
                        else if (highlight_nosystem)
                            settoblue = true;
                    }
                    else if (MFM_FileExist("C:\\WINDOWS\\system32\\" + pathstr))
                    {
                        if (pathstr.EndsWith(".exe"))
                            li.Icon = Icon.FromHandle(MGetExeIcon(@"C:\Windows\System32\" + pathstr));
                        StringBuilder exeCompany = new StringBuilder(256);
                        if (MGetExeCompany(@"C:\Windows\System32\" + pathstr, exeCompany, 256))
                        {
                            li.SubItems[3].Text = exeCompany.ToString();
                            if (highlight_nosystem && li.SubItems[3].Text != MICROSOFT)
                                settoblue = true;
                        }
                        else if (highlight_nosystem)
                            settoblue = true;
                    }
                    else if (MFM_FileExist("C:\\WINDOWS\\SysWOW64\\" + pathstr))
                    {
                        if (pathstr.EndsWith(".exe"))
                            li.Icon = Icon.FromHandle(MGetExeIcon(@"C:\Windows\SysWOW64\" + pathstr));
                        StringBuilder exeCompany = new StringBuilder(256);
                        if (MGetExeCompany(@"C:\Windows\SysWOW64\" + pathstr, exeCompany, 256))
                        {
                            li.SubItems[3].Text = exeCompany.ToString();
                            if (highlight_nosystem && li.SubItems[3].Text != MICROSOFT)
                                settoblue = true;
                        }
                        else if (highlight_nosystem)
                            settoblue = true;
                    }
                    else if (pathstr.StartsWith("wow64") && pathstr.EndsWith(".dll"))
                    {
#if !_X64_
                        if (!MIs64BitOS())
                        {
                            if (highlight_nosystem)
                                settoblue = true;
                            li.SubItems[3].Text = str_FileNotExist;
                        }
#endif
                        if (pathstr != "wow64.dll" && pathstr != "wow64cpu.dll" && pathstr != "wow64win.dll")
                        {
                            if (highlight_nosystem)
                                settoblue = true;
                            li.SubItems[3].Text = str_FileNotExist;
                        }
                    }
                    else
                    {
                        if (highlight_nosystem)
                            settoblue = true;
                        li.SubItems[3].Text = str_FileNotExist;
                    }
                }
            }

            string rootkey = Marshal.PtrToStringUni(MREG_ROOTKEYToStr(rootregpath));
            string regkey = rootkey + "\\" + Marshal.PtrToStringUni(regpath);
            string regvalues = Marshal.PtrToStringUni(regvalue);
            li.SubItems[4].Text = regkey + "\\" + regvalues;
            li.Tag = new startitem(filePath == null ? null : filePath.ToString(), rootregpath, Marshal.PtrToStringUni(regpath), regvalues);

            string typestr = Marshal.PtrToStringUni(type);
            if (typestr == "KnownDLLs")
            {
                li.Image = imageListFileTypeList.Images[".dll"];
                knowDlls.Childs.Add(li);
            }
            else if (typestr == "RightMenu1")
                rightMenu1.Childs.Add(li);
            else if (typestr == "RightMenu2")
                rightMenu2.Childs.Add(li);
            else if (typestr == "RightMenu3")
                rightMenu3.Childs.Add(li);
            else if (typestr == "PrintMonitors")
                printMonitors.Childs.Add(li);
            else if (typestr == "PrintProviders")
                printProviders.Childs.Add(li);

            else listStartup.Items.Add(li);
            if (settoblue)
                for (int i = 0; i < 5; i++)
                    li.SubItems[i].ForeColor = Color.Blue;
        }
        private void StartMListRemoveItem(uint id)
        {
            TaskMgrListItem target = null;
            foreach (TaskMgrListItem li in listStartup.Items)
            {
                startitem item = (startitem)li.Tag;
                if (item.id == id)
                {
                    target = li;
                    break;
                }
            }
            if (target != null)
                listStartup.Items.Remove(target);
        }

        private void listStartup_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (listStartup.SelectedItem != null)
                {
                    TaskMgrListItem selectedItem = listStartup.SelectedItem.OldSelectedItem == null ?
                 listStartup.SelectedItem : listStartup.SelectedItem.OldSelectedItem;
                    if (selectedItem.Type == TaskMgrListItemType.ItemMain)
                    {
                        startitem item = (startitem)selectedItem.Tag;
                        MStartupsMgr_ShowMenu(item.rootregpath, item.path, item.filepath, item.valuename, item.id, 0, 0);
                    }
                }
            }
        }
        private void listStartup_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Apps)
            {
                if (listStartup.SelectedItem != null)
                {
                    Point p = listStartup.GetiItemPoint(listStartup.SelectedItem);
                    p = listStartup.PointToScreen(p);
                    startitem item = (startitem)listStartup.SelectedItem.Tag;
                    MStartupsMgr_ShowMenu(item.rootregpath, item.path, item.filepath, item.valuename, item.id, p.X, p.Y);
                }
            }
        }

        #endregion

        #region KernelMWork

        private class ListViewItemComparerKr : IComparer
        {
            private int col;
            private bool asdening = false;

            public int SortColum { get { return col; } set { col = value; } }
            public bool Asdening { get { return asdening; } set { asdening = value; } }

            public int Compare(object o1, object o2)
            {
                ListViewItem x = o1 as ListViewItem, y = o2 as ListViewItem;
                int returnVal = -1;
                if (x.SubItems[col].Text == y.SubItems[col].Text) return -1;
                if (col == 6)
                {
                    int xi, yi;
                    if (int.TryParse(x.SubItems[col].Text, out xi) && int.TryParse(y.SubItems[col].Text, out yi))
                    {
                        if (x.SubItems[col].Text == y.SubItems[col].Text || xi < yi) returnVal = 0;
                        else if (xi > yi) returnVal = 1;
                        else if (xi < yi) returnVal = -1;
                    }
                }
                else returnVal = String.Compare(((ListViewItem)x).SubItems[col].Text, ((ListViewItem)y).SubItems[col].Text);
                if (asdening) returnVal = -returnVal;
                return returnVal;
            }
        }

        private FormKernel formKernel = null;
        private FormHooks formHooks = null;

        private ListViewItemComparerKr listViewItemComparerKr = new ListViewItemComparerKr();
        private bool showAllDriver = false;
        private bool canUseKernel = false;

        private void KernelListInit()
        {
            if (!driverListInited)
            {
                if (canUseKernel)
                {
                    enumKernelModulsCallBack = KernelEnumCallBack;
                    enumKernelModulsCallBackPtr = Marshal.GetFunctionPointerForDelegate(enumKernelModulsCallBack);

                    listViewItemComparerKr.SortColum = 6;
                    listDrivers.ListViewItemSorter = listViewItemComparerKr;
                    MAppWorkCall3(182, listDrivers.Handle, IntPtr.Zero);


                    KernelLisRefesh();
                }
                else
                {
                    listDrivers.Hide();
                    pl_driverNotLoadTip.Show();
                    linkRestartAsAdminDriver.Visible = !MIsRunasAdmin();
                }
                driverListInited = true;
            }
        }
        private void KernelEnumCallBack(IntPtr kmi, IntPtr BaseDllName, IntPtr FullDllPath, IntPtr FullDllPathOrginal, IntPtr szEntryPoint, IntPtr SizeOfImage, IntPtr szDriverObject, IntPtr szBase, IntPtr szServiceName, uint Order)
        {
            if (Order == 9999)
            {
                if (showAllDriver) lbDriversCount.Text = str_DriverCountLoaded + kmi.ToInt32() + "  " + str_DriverCount + BaseDllName.ToInt32();
                else
#if _X64_
                    lbDriversCount.Text = str_DriverCount + kmi.ToInt64();
#else
                    lbDriversCount.Text = str_DriverCount + kmi.ToInt32();
#endif

                return;
            }

            string baseDllName = Marshal.PtrToStringUni(BaseDllName);
            string fullDllPath = Marshal.PtrToStringUni(FullDllPath);

            ListViewItem li = new ListViewItem(baseDllName);
            li.Tag = kmi;
            //7 emepty items
            for (int i = 0; i < 8; i++) li.SubItems.Add(new ListViewItem.ListViewSubItem() { Font = listDrivers.Font });

            if (Order != 10000)
            {
                li.SubItems[0].Text = baseDllName;
                li.SubItems[1].Text = Marshal.PtrToStringUni(szBase);
                li.SubItems[2].Text = Marshal.PtrToStringUni(SizeOfImage);
                li.SubItems[3].Text = Marshal.PtrToStringUni(szDriverObject);
                li.SubItems[4].Text = fullDllPath;
                li.SubItems[5].Text = Marshal.PtrToStringUni(szServiceName);
                li.SubItems[6].Text = Order.ToString();
            }
            else
            {
                li.SubItems[0].Text = baseDllName;
                li.SubItems[1].Text = "-";
                li.SubItems[2].Text = "-";
                li.SubItems[3].Text = "-";
                li.SubItems[4].Text = fullDllPath;
                li.SubItems[5].Text = Marshal.PtrToStringUni(szServiceName);
                li.SubItems[6].Text = "-";
            }

            bool hightlight = false;
            if (MFM_FileExist(fullDllPath))
            {
                StringBuilder exeCompany = new StringBuilder(256);
                if (MGetExeCompany(fullDllPath, exeCompany, 256))
                {
                    li.SubItems[7].Text = exeCompany.ToString();
                    if (highlight_nosystem && exeCompany.ToString() != MICROSOFT)
                        hightlight = true;
                }
                else if (highlight_nosystem) hightlight = true;
                if (hightlight)
                {
                    li.ForeColor = Color.Blue;
                    foreach (ListViewItem.ListViewSubItem s in li.SubItems)
                        s.ForeColor = Color.Blue;
                }
            }
            else
            {
                li.SubItems[7].Text = str_FileNotExist;
                if (highlight_nosystem) hightlight = true;
            }
            if (hightlight)
            {
                li.ForeColor = Color.Blue;
                foreach (ListViewItem.ListViewSubItem s in li.SubItems)
                    s.ForeColor = Color.Blue;
            }

            listDrivers.Items.Add(li);
        }
        private void KernelLisRefesh()
        {
            if (canUseKernel)
            {
                foreach (ListViewItem li in listDrivers.Items)
                {
                    IntPtr kmi = (IntPtr)li.Tag;
                    if (kmi != IntPtr.Zero)
                        M_SU_EnumKernelModulsItemDestroy(kmi);
                }
                listDrivers.Items.Clear();
                M_SU_EnumKernelModuls(enumKernelModulsCallBackPtr, showAllDriver);
            }
        }
        private void KernelListUnInit()
        {
            foreach (ListViewItem li in listDrivers.Items)
            {
                IntPtr kmi = (IntPtr)li.Tag;
                if (kmi != IntPtr.Zero)
                    M_SU_EnumKernelModulsItemDestroy(kmi);
            }
            listDrivers.Items.Clear();
        }

        private void linkRestartAsAdminDriver_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            SetConfig("LoadKernelDriver", "Configure", "TRUE");
            MAppRebotAdmin2("select kernel");
        }
        private void linkLabelShowKernelTools_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (formKernel == null)
            {
                formKernel = new FormKernel();
                formKernel.FormClosed += FormKernel_FormClosed;
            }
            formKernel.Show();
            MAppWorkCall3(213, formKernel.Handle, IntPtr.Zero);
        }

        private void FormKernel_FormClosed(object sender, FormClosedEventArgs e)
        {
            formKernel = null;
        }
        private void FormHooks_FormClosed(object sender, FormClosedEventArgs e)
        {
            formHooks = null;
        }


        private void listDrivers_MouseUp(object sender, MouseEventArgs e)
        {
            if (listDrivers.SelectedItems.Count > 0)
            {
                if (e.Button == MouseButtons.Right) M_SU_EnumKernelModuls_ShowMenu((IntPtr)listDrivers.SelectedItems[0].Tag, showAllDriver, 0, 0);
            }
        }
        private void listDrivers_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            ((ListViewItemComparerKr)listDrivers.ListViewItemSorter).Asdening = !((ListViewItemComparerKr)listDrivers.ListViewItemSorter).Asdening;
            ((ListViewItemComparerKr)listDrivers.ListViewItemSorter).SortColum = e.Column;
            listDrivers.Sort();
        }
        private void listDrivers_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Apps)
            {
                if (listDrivers.SelectedItems.Count > 0)
                {
                    ListViewItem item = listDrivers.SelectedItems[0];
                    Point p = item.Position; p.X = 0;
                    p = listService.PointToScreen(p);
                    M_SU_EnumKernelModuls_ShowMenu((IntPtr)item.Tag, showAllDriver, p.X, p.Y);
                }
            }
        }

        public void ShowFormHooks()
        {
            if (formHooks == null)
            {
                formHooks = new FormHooks();
                formHooks.FormClosed += FormHooks_FormClosed;
            }
            formHooks.Show();
            MAppWorkCall3(213, formHooks.Handle, IntPtr.Zero);
        }

        #endregion

        #region NotifyWork

        private FormDelFileProgress delingdialog = null;

        private void DelingDialogInitHide()
        {
            MAppWorkCall3(200, delingdialog.Handle, IntPtr.Zero);
        }
        private void DelingDialogInit()
        {
            delingdialog = new FormDelFileProgress();
            delingdialog.Show(this);
        }
        private void DelingDialogClose()
        {
            delingdialog.Close();
            delingdialog = null;
        }
        private void ShowHideDelingDialog(bool show)
        {
            delingdialog.Invoke(new Action(delegate
            {
                delingdialog.Visible = show;
                if (show)
                {
                    delingdialog.Location = new Point(Left + Width / 2 - delingdialog.Width / 2, Top + Height / 2 - delingdialog.Height / 2);
                    delingdialog.Text = str_DeleteFiles;
                }
            }));
        }
        private void DelingDialogUpdate(string path, int value)
        {
            delingdialog.label.Invoke(new Action(delegate { delingdialog.label.Text = path; }));
            if (value == -1)
            {
                delingdialog.progressBar.Invoke(new Action(delegate { delingdialog.progressBar.Style = ProgressBarStyle.Marquee; }));
                delingdialog.Invoke(new Action(delegate
                {
                    delingdialog.Text = str_CollectingFiles;
                }));
            }
            else
            {
                delingdialog.progressBar.Invoke(new Action(delegate
                {
                    delingdialog.progressBar.Style = ProgressBarStyle.Blocks;
                    if (value >= 0 && value <= 100) delingdialog.progressBar.Value = value;
                }));
            }
        }

        private string lastVeryExe = "";
        private void FileTrustedLink_HyperlinkClick(object sender, HyperlinkEventArgs e)
        {
            if (!string.IsNullOrEmpty(lastVeryExe))
                MShowExeFileSignatureInfo(lastVeryExe);
        }

        private void StartingProgressShowHide(bool show)
        {
            lbStartingStatus.Invoke(new Action(delegate { lbStartingStatus.Visible = show; }));
            listProcess.Invoke(new Action(delegate { listProcess.Visible = !show; }));
        }
        private void StartingProgressUpdate(string text)
        {
            lbStartingStatus.Invoke(new Action(delegate { lbStartingStatus.Text = text; }));
        }

        private void ShowNoPdbWarn(string moduleName)
        {
            Invoke(new Action(delegate
            {
                TaskDialog noPdbWarnDialog = new TaskDialog(string.Format(LanuageMgr.GetStr("NoPDBWarn"), moduleName), str_TipTitle, string.Format(LanuageMgr.GetStr("NoPDBWarnText"), moduleName, moduleName));
                noPdbWarnDialog.EnableHyperlinks = true;
                noPdbWarnDialog.Show(this);
            }));
        }

        private bool TermintateImporantProcess(IntPtr name, int id)
        {
            TaskDialog taskDialog = null;
            if (id == 1)//强制结束警告
            {
                taskDialog = new TaskDialog(str_KillAskStart + " \"" + Marshal.PtrToStringUni(name) + "\" " + str_KillAskEnd, str_AppTitle, str_KillAskContentImporant);
                taskDialog.VerificationText = str_KillAskImporantGiveup;
                taskDialog.VerificationClick += TermintateImporantProcess_TaskDialog_VerificationClick;
                taskDialog.CustomButtons = new CustomButton[]
                {
                new CustomButton(1, str_Close),
                new CustomButton(2, str_Cancel),
                };
                taskDialog.EnableButton(1, false);
            }
            if (id == 2)//强制暂停警告
            {
                taskDialog = new TaskDialog(str_SuspendStart + " \"" + Marshal.PtrToStringUni(name) + "\" " + str_SuspendEnd, 
                    str_AppTitle, str_SuspendWarnContent);
                taskDialog.VerificationText = str_KillAskImporantGiveup;
                taskDialog.VerificationClick += TermintateImporantProcess_TaskDialog_VerificationClick;
                taskDialog.CustomButtons = new CustomButton[]
                {
                new CustomButton(1, str_Close),
                new CustomButton(2, str_Cancel),
                };
                taskDialog.EnableButton(1, false);
            }
            if (id == 3)//强制结束重要警告
            {
                taskDialog = new TaskDialog(str_KillAskStart + " \"" + Marshal.PtrToStringUni(name) + "\" " + str_KillAskEnd,
                    str_TitleVeryWarn, str_KillAskContentVeryImporant);
                taskDialog.VerificationText = str_KillAskImporantGiveup;
                taskDialog.VerificationClick += TermintateImporantProcess_TaskDialog_VerificationClick;
                taskDialog.CustomButtons = new CustomButton[]
                {
                new CustomButton(1, str_Close),
                new CustomButton(2, str_Cancel),
                };
                taskDialog.EnableButton(1, false);
            }
            if (id == 4)//强制暂停重要重要警告
            {
                taskDialog = new TaskDialog(str_SuspendStart + " \"" + Marshal.PtrToStringUni(name) + "\" " + str_SuspendEnd,
                    str_TitleVeryWarn, str_SuspendVeryImporantWarnContent);
                taskDialog.VerificationText = str_KillAskImporantGiveup;
                taskDialog.VerificationClick += TermintateImporantProcess_TaskDialog_VerificationClick;
                taskDialog.CustomButtons = new CustomButton[]
                {
                new CustomButton(1, str_Close),
                new CustomButton(2, str_Cancel),
                };
                taskDialog.EnableButton(1, false);
            }

            Results rs = taskDialog.Show(this);
            return rs.ButtonID == 1;
        }
        private void TermintateImporantProcess_TaskDialog_VerificationClick(object sender, CheckEventArgs e)
        {
            TaskDialog taskDialog = sender as TaskDialog;
            taskDialog.EnableButton(1, e.IsChecked);
        }

        #endregion

        #region Callbacks

        private static LanuageItems_CallBack lanuageItems_CallBack;

        private EnumProcessCallBack enumProcessCallBack;
        private EnumProcessCallBack2 enumProcessCallBack2;
        private EnumWinsCallBack enumWinsCallBack;
        private GetWinsCallBack getWinsCallBack;

        private IntPtr enumProcessCallBack_ptr;
        private IntPtr enumProcessCallBack2_ptr;
        private WNDPROC coreWndProc = null;
        private EXITCALLBACK exitCallBack;
        private WORKERCALLBACK workerCallBack;
        private TerminateImporantWarnCallBack terminateImporantWarnCallBack;
        private MFCALLBACK fileMgrCallBack;

        private EnumServicesCallBack scMgrEnumServicesCallBack;
        private IntPtr scMgrEnumServicesCallBackPtr = IntPtr.Zero;

        private EnumStartupsCallBack enumStartupsCallBack;
        private IntPtr enumStartupsCallBackPtr = IntPtr.Zero;

        private EnumKernelModulsCallBack enumKernelModulsCallBack;
        private IntPtr enumKernelModulsCallBackPtr = IntPtr.Zero;

        #endregion

        private void BaseProcessRefeshTimer_Tick(object sender, EventArgs e)
        {
            //整体刷新定时器
            if (!perfMainInited) ProcessListInitLater();
            if (!Visible) return;
            //base RefeshTimer
            if (tabControlMain.SelectedTab == tabPageProcCtl)
            {
                if (perfMainInited)
                {
                    //Refesh perfs
                    listProcess.Locked = true;
                    if (cpuindex != -1 || ramindex != -1 || diskindex != -1 || netindex != -1)
                        MPERF_GlobalUpdatePerformanceCounters();
                    if (cpuindex != -1)
                    {
                        int cpu = (int)(MPERF_GetCupUseAge());
                        listProcess.Colunms[cpuindex].TextBig = cpu + "%";
                        if (cpu >= 95)
                            listProcess.Colunms[cpuindex].IsHot = true;
                        else listProcess.Colunms[cpuindex].IsHot = false;
                    }
                    if (ramindex != -1)
                    {
                        int ram = (int)(MPERF_GetRamUseAge2() * 100);
                        listProcess.Colunms[ramindex].TextBig = ram + "%";
                        if (ram >= 95)
                            listProcess.Colunms[ramindex].IsHot = true;
                        else listProcess.Colunms[ramindex].IsHot = false;
                    }
                    if (diskindex != -1)
                    {
                        int disk = (int)(MPERF_GetDiskUseage() * 100);
                        listProcess.Colunms[diskindex].TextBig = disk + "%";
                        if (disk >= 95)
                            listProcess.Colunms[diskindex].IsHot = true;
                        else listProcess.Colunms[diskindex].IsHot = false;
                    }
                    if (netindex != -1)
                    {
                        int net = (int)(MPERF_GetNetWorkUseage() * 100);
                        listProcess.Colunms[netindex].TextBig = net + "%";
                        if (net >= 95)
                            listProcess.Colunms[netindex].IsHot = true;
                        else listProcess.Colunms[netindex].IsHot = false;
                    }
                }
                //Refesh Process List
                ProcessListRefesh2();
                listProcess.Locked = false;
                listProcess.Header.Invalidate();
            }
            else if (tabControlMain.SelectedTab == tabPagePerfCtl)
            {
                MPERF_GlobalUpdatePerformanceCounters();

                int cpu = (int)(MPERF_GetCupUseAge());
                perf_cpu.SmallText = cpu + " %";
                perf_cpu.AddData(cpu);

                perfPages[0].PageFroceSetData(cpu);

                int ram = (int)(MPERF_GetRamUseAge2() * 100);
                perf_ram.SmallText = ram + " %";
                perf_ram.AddData(ram);

                if (perfPages[1] != currSelectedPerformancePage)
                    perfPages[1].PageFroceSetData(ram);

                PerfUpdate();

                performanceLeftList.Invalidate();
            }
        }

        //Worker Callback
        private void AppWorkerCallBack(int msg, IntPtr wParam, IntPtr lParam)
        {
            //这是从 c++ 调用回来的函数
            switch (msg)
            {
                case 5:
                    {
                        int c = wParam.ToInt32();
                        if (c == 0)
                        {
                            baseProcessRefeshTimer.Interval = 0;
                            baseProcessRefeshTimer.Stop();
                            SetConfig("RefeshTime", "AppSetting", "Stop");
                            baseProcessRefeshTimerLow.Stop();
                            baseProcessRefeshTimerLowSc.Stop();
                            PerfUpdateGridUnit();
                        }
                        else
                        {
                            if (c == 1) { baseProcessRefeshTimer.Interval = 2000; SetConfig("RefeshTime", "AppSetting", "Slow"); }
                            else if (c == 2) { baseProcessRefeshTimer.Interval = 1000; SetConfig("RefeshTime", "AppSetting", "Fast"); }
                            baseProcessRefeshTimer.Start();
                            baseProcessRefeshTimerLow.Start();
                            baseProcessRefeshTimerLowSc.Start();
                            PerfUpdateGridUnit();
                        }
                        break;
                    }
                case 6:
                    {
                        int c = wParam.ToInt32();
                        if (c == 0)
                        {
                            SetConfig("TopMost", "AppSetting", "FALSE");
                            TopMost = false;
                        }
                        else if (c == 1)
                        {
                            SetConfig("TopMost", "AppSetting", "TRUE");
                            TopMost = true;
                        }
                        break;
                    }
                case 7:
                    {
                        int c = wParam.ToInt32();
                        if (c == 0)
                        {
                            SetConfig("CloseHideToNotfication", "AppSetting", "FALSE");
                            close_hide = false;
                        }
                        else if (c == 1)
                        {
                            SetConfig("CloseHideToNotfication", "AppSetting", "TRUE");
                            close_hide = true;
                        }
                        break;
                    }
                case 8:
                    {
                        int c = wParam.ToInt32();
                        if (c == 0)
                        {
                            SetConfig("MinHide", "AppSetting", "FALSE");
                            min_hide = false;
                        }
                        else if (c == 1)
                        {
                            SetConfig("MinHide", "AppSetting", "TRUE");
                            min_hide = true;
                        }
                        break;
                    }
                case 9:
                    {
                        string scname = Marshal.PtrToStringUni(wParam);
                        tabControlMain.SelectedTab = tabPageScCtl;
                        foreach (ListViewItem it in listService.Items)
                        {
                            if (it.Text == scname)
                            {
                                int i = listService.Items.IndexOf(it);
                                listService.EnsureVisible(i);
                                it.Selected = true;
                            }
                            else it.Selected = false;
                        }
                        break;
                    }
                case 10:
                    {
                        ScMgrRefeshList();
                        break;
                    }
                case 11:
                    {

                        break;
                    }
                case 12:
                    {
                        new FormSpyWindow(wParam).ShowDialog();
                        break;
                    }
                case 13:
                    {
                        new FormFileTool().ShowDialog();
                        break;
                    }
                case 14:
                    {
                        new FormAbout().ShowDialog();
                        break;
                    }
                case 15:
                    {
                        uint pid = Convert.ToUInt32(wParam.ToInt32());
                        ProcessListEndTask(pid, null);
                        break;
                    }
                case 16:
                    {
                        new FormLoadDriver().Show();
                        break;
                    }
                case 17:
                    {

                        break;
                    }
                case 18:
                    {
                        ShowHideDelingDialog(true);
                        break;
                    }
                case 19:
                    {
                        ShowHideDelingDialog(false);
                        DelingDialogUpdate(str_DeleteFiles, 0);
                        break;
                    }
                case 20:
                    {
                        DelingDialogUpdate(Marshal.PtrToStringUni(wParam), lParam.ToInt32());
                        break;
                    }
                case 21:
                    {
                        DelingDialogUpdate(str_CollectingFiles, -1);
                        break;
                    }
                case 22:
                    {
                        AppWorkerCallBack(41, IntPtr.Zero, IntPtr.Zero);
                        if (MInitKernel(null))
                            if (GetConfigBool("SelfProtect", "AppSetting"))
                                MAppWorkCall3(203, IntPtr.Zero, IntPtr.Zero);
                        break;
                    }
                case 23:
                    {
                        new FormVHandles(Convert.ToUInt32(wParam.ToInt32()), Marshal.PtrToStringUni(lParam)).ShowDialog();
                        break;
                    }
                case 24:
                    {
                        KernelListInit();
                        break;
                    }
                case 25:
                    {
                        showAllDriver = !showAllDriver;
                        KernelLisRefesh();
                        break;
                    }
                case 26:
                    {
                        StartMListRemoveItem(Convert.ToUInt32(wParam.ToInt32()));
                        break;
                    }
                case 27:
                    {
                        new FormVKrnInfo(Convert.ToUInt32(wParam.ToInt32()), Marshal.PtrToStringUni(lParam)).ShowDialog();
                        break;
                    }
                case 28:
                    {
                        //timer
                        new FormVTimers(Convert.ToUInt32(wParam.ToInt32()));
                        break;
                    }
                case 29:
                    {
                        //hotkey
                        new FormVHotKeys(Convert.ToUInt32(wParam.ToInt32()));
                        break;
                    }
                case 30:
                    {
                        string path = Marshal.PtrToStringUni(wParam);
                        lastVeryExe = path;
                        TaskDialog d = new TaskDialog(str_FileTrust, str_TipTitle, (path == null ? "" : path) + "\n\n" + str_FileTrustViewCrt);
                        d.EnableHyperlinks = true;
                        d.HyperlinkClick += FileTrustedLink_HyperlinkClick;
                        d.Show(this);
                        break;
                    }
                case 31:
                    {
                       
                        break;
                    }
                case 32:
                    {
                        new FormKDA().ShowDialog(this);
                        break;
                    }
                case 33:
                    {

                        break;
                    }
                case 34:
                    {
                        StartingProgressUpdate(Marshal.PtrToStringUni(wParam));
                        break;
                    }
                case 35:
                    {
                        ShowNoPdbWarn(Marshal.PtrToStringAnsi(wParam));
                        break;
                    }
                case 36:
                    {
                        Invoke(new Action(AppLastLoadStep));
                        break;
                    }
                case 37:
                    {
                        if (kDbgPrint == null)
                        {
                            kDbgPrint = new FormKDbgPrint();
                            kDbgPrint.FormClosed += KDbgPrint_FormClosed;
                        }
                        kDbgPrint.Show();
                        break;
                    }
                case 38:
                    {
                        if (kDbgPrint != null && !exitkDbgPrintCalled)
                        {
                            exitkDbgPrintCalled = true;
                            kDbgPrint.Close();
                            kDbgPrint = null;
                            exitkDbgPrintCalled = false;
                        }
                        break;
                    }
                case 39:
                    {
                        if (kDbgPrint != null)
                            kDbgPrint.Add(Marshal.PtrToStringUni(wParam));
                        break;
                    }
                case 40:
                    {
                        if (kDbgPrint != null)
                            kDbgPrint.Add("");
                        break;
                    }
                case 41:
                    {
                        if (listProcess.Visible) listProcess.Invoke(new Action(listProcess.Hide));
                        StartingProgressShowHide(true);
                        break;
                    }
                case 42:
                    {
                        if (!listProcess.Visible) listProcess.Invoke(new Action(listProcess.Show));
                        StartingProgressShowHide(false);
                        break;
                    }
                case 51:
                    {
                        new FormVPrivilege(Convert.ToUInt32(wParam.ToInt32()), Marshal.PtrToStringUni(lParam)).ShowDialog();
                        break;
                    }
                case 52:
                    {
                        linkLabelShowKernelTools_LinkClicked(this, null);
                        break;
                    }
                case 53:
                    {
                        ShowFormHooks();
                        break;
                    }
                case 54:
                    {
                        //netmon
                        break;
                    }
                case 55:
                    {
                        //regedit
                        break;
                    }
                case 56:
                    {
                        tabControlMain.SelectedTab = tabPageFileCtl;
                        break;
                    }
                case 57:
                    {
                        
                        break;
                    }
                case 58:
                    {
                        if (wParam.ToInt32() == 1)
                        {
                            TaskMgrListItem li = listApps.SelectedItem;
                            if (li == null) return;
                            ProcessListEndTask(0, li);
                        }
                        else if (wParam.ToInt32() == 0)
                        {
                            TaskMgrListItem li = listApps.SelectedItem;
                            if (li == null) return;
                            ProcessListSetTo(li);
                        }
                        break;
                    }
            }
        }

        //Load and exit
        private void AppLoad()
        {
            //初始化函数
            DelingDialogInit();
            DelingDialogInitHide();

            Log("Loading callbacks...");

            exitCallBack = AppExit;
            terminateImporantWarnCallBack = TermintateImporantProcess;
            enumProcessCallBack = ProcessListHandle;
            enumWinsCallBack = MainEnumWinsCallBack;
            getWinsCallBack = MainGetWinsCallBack;
            enumProcessCallBack2 = ProcessListHandle2;
            workerCallBack = AppWorkerCallBack;
            lanuageItems_CallBack = Native_LanuageItems_CallBack;

             MAppSetCallBack(Marshal.GetFunctionPointerForDelegate(exitCallBack), 1);
            MAppSetCallBack(Marshal.GetFunctionPointerForDelegate(terminateImporantWarnCallBack), 2);
            MAppSetCallBack(Marshal.GetFunctionPointerForDelegate(enumWinsCallBack), 3);
            MAppSetCallBack(Marshal.GetFunctionPointerForDelegate(getWinsCallBack), 4);
            MAppSetCallBack(Marshal.GetFunctionPointerForDelegate(workerCallBack), 5);
            MLG_SetLanuageItems_CallBack(Marshal.GetFunctionPointerForDelegate(lanuageItems_CallBack));

            MAppWorkCall3(181, IntPtr.Zero, IntPtr.Zero);
            MAppWorkCall3(183, Handle, IntPtr.Zero);
            coreWndProc = (WNDPROC)Marshal.GetDelegateForFunctionPointer(MAppSetCallBack(IntPtr.Zero, 0), typeof(WNDPROC));

            SetConfig("LastWindowTitle", "AppSetting", Text);

            Log("Loading Settings...");

            LoadSettings();

            SysVer.Get();
            if (!SysVer.IsWin8Upper()) tabControlMain.TabPages.Remove(tabPageUWPCtl);

            #region GetSettings


            string sortamxx = GetConfig("ListSortDk", "AppSetting");
            if (sortamxx != "")
                if (sortamxx == "TRUE")
                    sorta = true;
            string sortitemxx = GetConfig("ListSortIndex", "AppSetting");
            if (sortitemxx != "" && sortitemxx != "-1")
            {
                try
                {
                    sortitem = int.Parse(sortitemxx);
                }
                catch { }
            }
            showHiddenFiles = GetConfig("ShowHiddenFiles", "AppSetting") == "TRUE";
            MFM_SetShowHiddenFiles(showHiddenFiles);
            #endregion

            if (!MGetPrivileges()) TaskDialog.Show(LanuageMgr.GetStr("FailedGetPrivileges"), str_AppTitle, "", TaskDialogButton.OK, TaskDialogIcon.Warning);
#if _X64_
            Log("64 Bit OS ");
            is64OS = true;
#else
            is64OS = MIs64BitOS();
            Log(is64OS ? "64 Bit OS but 32 bit app " : "32 Bit OS");
#endif

            Log("Loading...");

            LoadList();

            if (MIsRunasAdmin())
                AppLoadKernel();
            else AppLastLoadStep();
        }
        private void AppExit()
        {
            //退出函数
            Log("App exit...");
            AppOnExit();
            Application.Exit();
        }

        #region FormEvent

        private void AppLastLoadStep()
        {
            int id = AppRunAgrs();
            switch (id)
            {
                case 1:
                    tabControlMain.SelectedTab = tabPageKernelCtl;
                    break;
                case 3:
                    tabControlMain.SelectedTab = tabPagePerfCtl;
                    break;
                case 4:
                    tabControlMain.SelectedTab = tabPageUWPCtl;
                    break;
                case 5:
                    tabControlMain.SelectedTab = tabPageScCtl;
                    break;
                case 6:
                    tabControlMain.SelectedTab = tabPageStartCtl;
                    break;
                case 7:
                    tabControlMain.SelectedTab = tabPageFileCtl;
                    break;
                case 8:
                    return;
                case 0:
                default:
                    ProcessListInitIn1Slater();
                    break;
            }
        }
        private void AppLoadKernel()
        {
            if (GetConfigBool("LoadKernelDriver", "Configure"))
            {
                Log("Loading Kernel...");
                if (!MInitKernel(null))
                {
                    if (eprocessindex != -1)
                    {
                        listProcess.Colunms.Remove(listProcess.Colunms[eprocessindex]);
                        eprocessindex = -1;
                    }

                    if(MIsKernelNeed64())
                        TaskDialog.Show(LanuageMgr.GetStr("LoadDriverErrNeed64"), LanuageMgr.GetStr("ErrTitle"), LanuageMgr.GetStr("LoadDriverErrNeed64Text"), TaskDialogButton.OK, TaskDialogIcon.None);
                    else TaskDialog.Show(LanuageMgr.GetStr("LoadDriverErr"), LanuageMgr.GetStr("ErrTitle"), "", TaskDialogButton.OK, TaskDialogIcon.None);
                    AppLastLoadStep();
                }
                else
                {
                    if (GetConfigBool("SelfProtect", "AppSetting"))
                        MAppWorkCall3(203, IntPtr.Zero, IntPtr.Zero);
                }
                canUseKernel = MCanUseKernel();
            }
            else
            {
                AppLastLoadStep();
                if (eprocessindex != -1)
                {
                    listProcess.Colunms.Remove(listProcess.Colunms[eprocessindex]);
                    eprocessindex = -1;
                }
            }
        }
        private void AppOnExit()
        {
            if (!exitCalled)
            {
                baseProcessRefeshTimer.Stop();

                ProcessListSimpleExit();
                AppWorkerCallBack(38, IntPtr.Zero, IntPtr.Zero);
                DelingDialogClose();
                MPERF_NET_FreeAllProcessNetInfo();
                PerfClear();
                ProcessListUnInitPerfs();
                ProcessListFreeAll();
                MSCM_Exit();
                MEnumProcessFree();
                KernelListUnInit();
                fileSystemWatcher.EnableRaisingEvents = false;
                M_LOG_Close();
                MAppStartEnd();
                MAppWorkCall3(204, IntPtr.Zero, IntPtr.Zero);
                MAppWorkCall3(207, Handle, IntPtr.Zero);
                exitCalled = true;
            }
        }

        private int AppRunAgrs()
        {
            if (agrs.Length > 0)
            {
                Log("App Agrs 0 : " + agrs[0]);
                if (agrs[0] == "select" && agrs.Length > 1)
                {
                    Log("App Agrs 1 : " + agrs[1]);
                    if (agrs[1] == "kernel")
                        return 1;
                    if (agrs[1] == "perf")
                        return 3;
                    if (agrs[1] == "uwpapps")
                        return 4;
                    if (agrs[1] == "services")
                        return 5;
                    if (agrs[1] == "startmgr")
                        return 6;
                    if (agrs[1] == "filemgr")
                        return 7;
                }
                else if (agrs[0] == "spy")
                    new FormSpyWindow(GetDesktopWindow()).ShowDialog();
                else if (agrs[0] == "filetool")
                    new FormFileTool().ShowDialog();
                else if (agrs[0] == "loaddriver")
                    new FormLoadDriver().ShowDialog();
            }
            return 0;
        }

        private FormKDbgPrint kDbgPrint = null;
        private bool exitkDbgPrintCalled = false;
        private void KDbgPrint_FormClosed(object sender, FormClosedEventArgs e)
        {
            kDbgPrint = null;
        }

        private bool exitCalled = false;
        private int showHideHotKetId = 0;

        private bool close_hide = false;
        private bool min_hide = false;
        private bool highlight_nosystem = false;

        private void LoadList()
        {
            lvwColumnSorter = new TaskListViewColumnSorter(this);

            TaskMgrListGroup lg = new TaskMgrListGroup(LanuageMgr.GetStr("TitleApp"));
            listProcess.Groups.Add(lg);
            TaskMgrListGroup lg2 = new TaskMgrListGroup(LanuageMgr.GetStr("TitleBackGround"));
            listProcess.Groups.Add(lg2);
            TaskMgrListGroup lg3 = new TaskMgrListGroup(LanuageMgr.GetStr("TitleWinApp"));
            listProcess.Groups.Add(lg3);

            listUwpApps.Header.Height = 36;
            listUwpApps.ReposVscroll();
            listStartup.Header.Height = 36;
            listStartup.ReposVscroll();
            //listStartup.DrawIcon = false;
            TaskMgrListHeaderItem li = new TaskMgrListHeaderItem();
            li.TextSmall = LanuageMgr.GetStr("TitleName");
            li.Width = 200;
            listStartup.Colunms.Add(li);
            TaskMgrListHeaderItem li2 = new TaskMgrListHeaderItem();
            li2.TextSmall = LanuageMgr.GetStr("TitleCmdLine");
            li2.Width = 200;
            listStartup.Colunms.Add(li2);
            TaskMgrListHeaderItem li3 = new TaskMgrListHeaderItem();
            li3.TextSmall = LanuageMgr.GetStr("TitleFilePath");
            li3.Width = 200;
            listStartup.Colunms.Add(li3);
            TaskMgrListHeaderItem li4 = new TaskMgrListHeaderItem();
            li4.TextSmall = LanuageMgr.GetStr("TitlePublisher");
            li4.Width = 100;
            listStartup.Colunms.Add(li4);
            TaskMgrListHeaderItem li5 = new TaskMgrListHeaderItem();
            li5.TextSmall = LanuageMgr.GetStr("TitleRegPath");
            li5.Width = 200;
            listStartup.Colunms.Add(li5);

            TaskMgrListHeaderItem li8 = new TaskMgrListHeaderItem();
            li8.TextSmall = LanuageMgr.GetStr("TitleName");
            li8.Width = 400;
            listUwpApps.Colunms.Add(li8);
            TaskMgrListHeaderItem li9 = new TaskMgrListHeaderItem();
            li9.TextSmall = LanuageMgr.GetStr("TitlePublisher");
            li9.Width = 100;
            listUwpApps.Colunms.Add(li9);
            TaskMgrListHeaderItem li12 = new TaskMgrListHeaderItem();
            li12.TextSmall = LanuageMgr.GetStr("TitleDescription");
            li12.Width = 100;
            listUwpApps.Colunms.Add(li12);
            TaskMgrListHeaderItem li10 = new TaskMgrListHeaderItem();
            li10.TextSmall = LanuageMgr.GetStr("TitleFullName");
            li10.Width = 130;
            listUwpApps.Colunms.Add(li10);
            TaskMgrListHeaderItem li11 = new TaskMgrListHeaderItem();
            li11.TextSmall = LanuageMgr.GetStr("TitleInstallDir");
            li11.Width = 130;
            listUwpApps.Colunms.Add(li11);

            string s1 = GetConfig("MainHeaders1", "AppSetting");
            if (s1 != "") listProcessAddHeader("TitleName", int.Parse(s1));
            else listProcessAddHeader("TitleName", 200);
            string headers = GetConfig("MainHeaders", "AppSetting");
            if (headers.Contains("#"))
            {
                string[] headersv = headers.Split('#');
                for (int i = 0; i < headersv.Length; i++)
                {
                    if (headersv[i].Contains("-"))
                    {
                        string[] headersvx = headersv[i].Split('-');
                        listProcessAddHeader(headersvx[0], int.Parse(headersvx[1]));
                    }
                }
            }
            else if (headers == "")
            {
                listProcessAddHeader("TitleStatus", 70);
                listProcessAddHeader("TitlePID", 55);
                listProcessAddHeader("TitleProcName", 130);
                listProcessAddHeader("TitleCPU", 75);
                listProcessAddHeader("TitleRam", 75);
                listProcessAddHeader("TitleDisk", 75);
                listProcessAddHeader("TitleNet", 75);
            }

            LoadRefeshRateSetting();

            MAppWorkCall3(194, IntPtr.Zero, GetConfigBool("TopMost", "AppSetting", false) ? new IntPtr(1) : IntPtr.Zero);
            MAppWorkCall3(195, IntPtr.Zero, GetConfigBool("CloseHideToNotfication", "AppSetting", false) ? new IntPtr(1) : IntPtr.Zero);
            MAppWorkCall3(196, IntPtr.Zero, GetConfigBool("MinHide", "AppSetting", false) ? new IntPtr(1) : IntPtr.Zero);

            nameindex = listProcessGetListIndex("TitleProcName");
            companyindex = listProcessGetListIndex("TitlePublisher");
            stateindex = listProcessGetListIndex("TitleStatus");
            pidindex = listProcessGetListIndex("TitlePID");
            cpuindex = listProcessGetListIndex("TitleCPU");
            ramindex = listProcessGetListIndex("TitleRam");
            diskindex = listProcessGetListIndex("TitleDisk");
            netindex = listProcessGetListIndex("TitleNet");
            pathindex = listProcessGetListIndex("TitleProcPath");
            cmdindex = listProcessGetListIndex("TitleCmdLine");
            eprocessindex = listProcessGetListIndex("TitleEProcess");

            if (pidindex != -1) listProcess.Header.Items[pidindex].Alignment = StringAlignment.Far;
            if (cpuindex != -1)
            {
                listProcess.Header.Items[cpuindex].IsNum = true;
                listProcess.Header.Items[cpuindex].Alignment = StringAlignment.Far;
            }
            if (ramindex != -1)
            {
                listProcess.Header.Items[ramindex].IsNum = true;
                listProcess.Header.Items[ramindex].Alignment = StringAlignment.Far;
            }
            if (diskindex != -1)
            {
                listProcess.Header.Items[diskindex].IsNum = true;
                listProcess.Header.Items[diskindex].Alignment = StringAlignment.Far;
            }
            if (netindex != -1)
            {
                listProcess.Header.Items[netindex].IsNum = true;
                listProcess.Header.Items[netindex].Alignment = StringAlignment.Far;
            }
        }
        private void LoadSettings()
        {
            MAppWorkCall3(206, IntPtr.Zero, new IntPtr(GetConfig("TerProcFun", "Configure", "PspTerProc") == "ApcPspTerProc" ? 1 : 0));
            highlight_nosystem = GetConfigBool("HighLightNoSystetm", "Configure", false);
            mergeApps = GetConfigBool("MergeApps", "Configure", true);
        }
        private void LoadLastPos()
        {
            if (GetConfigBool("OldIsMax", "AppSetting"))
                WindowState = FormWindowState.Maximized;
            else
            {
                bool s_isSimpleView = GetConfigBool("SimpleView", "AppSetting", true);

                string p = GetConfig("OldPos", "AppSetting");
                if (p.Contains("-"))
                {
                    string[] pp = p.Split('-');
                    try
                    {
                        Left = int.Parse(pp[0]);
                        Top = int.Parse(pp[1]);
                        if (Left > Screen.PrimaryScreen.Bounds.Width)
                            Left = 100;
                        if (Top > Screen.PrimaryScreen.Bounds.Height)
                            Top = 200;
                    }
                    catch { }
                }

                string sl = GetConfig("OldSizeSimple", "AppSetting","380-334");
                if (sl.Contains("-"))
                {
                    string[] ss = sl.Split('-');
                    try
                    {
                        int w = int.Parse(ss[0]); if (w + Left > Screen.PrimaryScreen.WorkingArea.Width) w = Screen.PrimaryScreen.WorkingArea.Width - Left;
                        int h = int.Parse(ss[1]); if (h + Top > Screen.PrimaryScreen.WorkingArea.Height) h = Screen.PrimaryScreen.WorkingArea.Height - Top;
                        lastSimpleSize = new Size(w, h);

                        if (s_isSimpleView)
                        {
                            Width = w;
                            Height = h;
                        }
                    }
                    catch { }
                }
                string s = GetConfig("OldSize", "AppSetting","780-500");
                if (s.Contains("-"))
                {
                    string[] ss = s.Split('-');
                    try
                    {
                        int w = int.Parse(ss[0]); if (w + Left > Screen.PrimaryScreen.WorkingArea.Width) w = Screen.PrimaryScreen.WorkingArea.Width - Left;
                        int h = int.Parse(ss[1]); if (h + Top > Screen.PrimaryScreen.WorkingArea.Height) h = Screen.PrimaryScreen.WorkingArea.Height - Top;
                        lastSize = new Size(w, h);
                        if (!s_isSimpleView)
                        {
                            Width = w;
                            Height = h;
                        }
                    }
                    catch { }
                }
            }
        }
        private void LoadHotKey()
        {
            if (GetConfigBool("HotKey", "AppSetting", true))
            {
                string k1 = GetConfig("HotKey1", "AppSetting", "(None)");
                string k2 = GetConfig("HotKey2", "AppSetting", "T");
                if (k1 == "(None)") k1 = "None";
                Keys kv1, kv2;
                try
                {
                    if (k1 != "(None)") kv1 = (Keys)Enum.Parse(typeof(Keys), k1);
                    else kv1 = Keys.None;
                    kv2 = (Keys)Enum.Parse(typeof(Keys), k2);
                }
                catch (Exception e)
                {
                    LogErr("Invalid hotkey settings : " + e.Message);
                    kv2 = Keys.T;
                    kv1 = Keys.Shift;
                }

                showHideHotKetId = MAppRegShowHotKey(Handle, (uint)(int)kv1, (uint)(int)kv2);
                MAppWorkCall3(209, Handle, IntPtr.Zero);
            }
        }
        private void LoadRefeshRateSetting()
        {
            string s2 = GetConfig("RefeshTime", "AppSetting");
            switch (s2)
            {
                case "Stop":
                    MAppWorkCall3(193, IntPtr.Zero, new IntPtr(0));
                    break;
                case "Slow":
                    MAppWorkCall3(193, IntPtr.Zero, new IntPtr(1));
                    break;
                case "Fast":
                    MAppWorkCall3(193, IntPtr.Zero, new IntPtr(2));
                    break;
            }
        }

        private void notifyIcon_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            Show();
        }
        private void FormMain_Shown(object sender, EventArgs e)
        {
            AppLoad();
        }
        private void FormMain_Load(object sender, EventArgs e)
        {
            Text = GetConfig("Title", "AppSetting", "任务管理器");

            if (Text == "") Text = str_AppTitle;

            LoadHotKey();
            LoadLastPos();
        }
        private void FormMain_Activated(object sender, EventArgs e)
        {
            listUwpApps.FocusedType = true;
            listStartup.FocusedType = true;
            listProcess.FocusedType = true;
        }
        private void FormMain_Deactivate(object sender, EventArgs e)
        {
            listUwpApps.FocusedType = false;
            listStartup.FocusedType = false;
            listProcess.FocusedType = false;
        }
        private void FormMain_OnWmCommand(int id)
        {
            switch (id)
            {
                //def in resource.h
                case 40005:
                    {
                        new FormSettings(this).ShowDialog();
                        break;
                    }
                case 40034:
                    {
                        if (tabControlMain.SelectedTab == tabPageProcCtl)
                        {
                            WorkWindow.FormMainListHeaders f = new WorkWindow.FormMainListHeaders(this);
                            if (f.ShowDialog() == DialogResult.OK)
                                MAppWorkCall3(191, IntPtr.Zero, IntPtr.Zero);
                        }
                        break;
                    }
                case 41130:
                case 41012:
                    {
                        if (tabControlMain.SelectedTab == tabPageProcCtl)
                            ProcessListRefesh();
                        else if (tabControlMain.SelectedTab == tabPageKernelCtl)
                            KernelLisRefesh();
                        else if (tabControlMain.SelectedTab == tabPageStartCtl)
                            StartMListRefesh();
                        else if (tabControlMain.SelectedTab == tabPageScCtl)
                            ScMgrRefeshList();
                        else if (tabControlMain.SelectedTab == tabPageFileCtl)
                            FileMgrShowFiles(null);
                        else if (tabControlMain.SelectedTab == tabPageUWPCtl)
                            UWPListRefesh();
                        else if (tabControlMain.SelectedTab == tabPagePerfCtl)
                            BaseProcessRefeshTimer_Tick(null, null);
                        break;
                    }
                case 40019:
                    {
                        TaskDialog t = new TaskDialog(LanuageMgr.GetStr("TitleReboot"), str_AppTitle, LanuageMgr.GetStr("TitleContinue"), TaskDialogButton.Yes | TaskDialogButton.No, TaskDialogIcon.Warning);
                        if (t.Show(this).CommonButton == Result.Yes)
                            MAppWorkCall3(185, IntPtr.Zero, IntPtr.Zero);
                        break;
                    }
                case 41020:
                    {
                        TaskDialog t = new TaskDialog(LanuageMgr.GetStr("TitleLogoOff"), str_AppTitle, LanuageMgr.GetStr("TitleContinue"), TaskDialogButton.Yes | TaskDialogButton.No, TaskDialogIcon.Warning);
                        if (t.Show(this).CommonButton == Result.Yes)
                            MAppWorkCall3(186, IntPtr.Zero, IntPtr.Zero);
                        break;
                    }
                case 40018:
                    {
                        TaskDialog t = new TaskDialog(LanuageMgr.GetStr("TitleShutdown"), str_AppTitle, LanuageMgr.GetStr("TitleContinue"), TaskDialogButton.Yes | TaskDialogButton.No, TaskDialogIcon.Warning);
                        if (t.Show(this).CommonButton == Result.Yes)
                            MAppWorkCall3(187, IntPtr.Zero, IntPtr.Zero);
                        break;
                    }
                case 41151:
                    {
                        TaskDialog t = new TaskDialog(LanuageMgr.GetStr("TitleFShutdown"), str_AppTitle, LanuageMgr.GetStr("TitleContinue"), TaskDialogButton.Yes | TaskDialogButton.No, TaskDialogIcon.Warning);
                        if (t.Show(this).CommonButton == Result.Yes)
                            MAppWorkCall3(201, IntPtr.Zero, IntPtr.Zero);
                        break;
                    }
                case 41152:
                    {
                        TaskDialog t = new TaskDialog(LanuageMgr.GetStr("TitleFRebbot"), str_AppTitle, LanuageMgr.GetStr("TitleContinue"), TaskDialogButton.Yes | TaskDialogButton.No, TaskDialogIcon.Warning);
                        if (t.Show(this).CommonButton == Result.Yes)
                            MAppWorkCall3(202, IntPtr.Zero, IntPtr.Zero);
                        break;
                    }
                case 41153:
                    {
                        break;
                    }
            }
        }
        private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (close_hide)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            SetConfig("ListSortIndex", "AppSetting", sortitem.ToString());
            if (sorta) SetConfig("ListSortDk", "AppSetting", "TRUE");
            else SetConfig("ListSortDk", "AppSetting", "FALSE");
            if (!isSimpleView)
                SetConfig("OldSize", "AppSetting", Width.ToString() + "-" + Height.ToString());
            else SetConfig("OldSize", "AppSetting", lastSize.Width.ToString() + "-" + lastSize.Height.ToString());
            SetConfig("OldPos", "AppSetting", Left.ToString() + "-" + Top.ToString());
            SetConfigBool("OldIsMax", "AppSetting", WindowState == FormWindowState.Maximized);

            if (saveheader)
            {
                string headers = "";
                for (int i = 1; i < listProcess.Colunms.Count; i++)
                    headers = headers + "#" + listProcess.Colunms[i].Identifier + "-" + listProcess.Colunms[i].Width;
                SetConfig("MainHeaders", "AppSetting", headers);
            }
            SetConfig("MainHeaders1", "AppSetting", listProcess.Colunms[0].Width.ToString());

            AppOnExit();
        }
        private void FormMain_OnWmHotKey(int id)
        {
            if (id == showHideHotKetId)
            {
                if (!IsWindowVisible(Handle))
                    MAppWorkCall3(208, Handle, IntPtr.Zero);
            }
        }
        private void FormMain_VisibleChanged(object sender, EventArgs e)
        {
            if(Visible)
            {
                listProcess.Locked = false;
                if(processListInited)
                BaseProcessRefeshTimer_Tick(sender, e);               
            }
            else
            {
                listProcess.Locked = true;
            }
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_COMMAND)
                FormMain_OnWmCommand(m.WParam.ToInt32());
            else if (m.Msg == WM_HOTKEY)
                FormMain_OnWmHotKey(m.WParam.ToInt32());
            else if (m.Msg == WM_SYSCOMMAND)
            {
                if (min_hide && m.WParam.ToInt32() == 0xF20)//SC_MINIMIZE
                    Hide();
            }
            coreWndProc?.Invoke(m.HWnd, Convert.ToUInt32(m.Msg), m.WParam, m.LParam);
        }

        public static void AppHWNDSendMessage(uint message, IntPtr wParam, IntPtr lParam)
        {
            MAppWorkCall2(message, wParam, lParam);
        }

        #endregion

        private void tabControlMain_Selected(object sender, TabControlEventArgs e)
        {
            if (e.TabPage == tabPageProcCtl)
            {
                ProcessListInit();
            }
            else if (e.TabPage == tabPageScCtl)
            {
                ScMgrInit();
            }
            else if (e.TabPage == tabPageFileCtl)
            {
                FileMgrInit();
            }
            else if (e.TabPage == tabPageUWPCtl)
            {
                UWPListInit();
            }
            else if (e.TabPage == tabPagePerfCtl)
            {
                PerfInit();
            }
            else if (e.TabPage == tabPageStartCtl)
            {
                StartMListInit();
            }
            else if (e.TabPage == tabPageKernelCtl)
            {
                KernelListInit();
            }
        }
    }
}
