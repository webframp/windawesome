﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Windawesome
{
	public sealed class Workspace
	{
		public readonly int id;
		public ILayout Layout { get; private set; }
		public readonly BarInfo[] barsAtTop;
		public readonly BarInfo[] barsAtBottom;
		public readonly string name;
		public bool ShowWindowsTaskbar { get; private set; }
		public bool IsCurrentWorkspace { get; private set; }
		public readonly bool repositionOnSwitchedTo;
		public static readonly IntPtr taskbarHandle;
		public static readonly IntPtr startButtonHandle;

		private static readonly HashSet<IBar> registeredBars = new HashSet<IBar>();

		internal class NativeWindowWithHeight : NativeWindow
		{
			public int Height;

			public NativeWindowWithHeight(int height)
			{
				this.Height = height;
				this.CreateHandle(new CreateParams { Height = height, Parent = NativeMethods.HWND_MESSAGE });
			}
		}

		internal static NativeWindowWithHeight[] appBarTopWindows;
		internal static NativeWindowWithHeight[] appBarBottomWindows;

		public class BarInfo
		{
			public readonly IBar bar;
			public bool ShowBar { get; internal set; }

			public BarInfo(IBar bar, bool showBar = true)
			{
				this.bar = bar;
				this.ShowBar = showBar;
			}

			public override int GetHashCode()
			{
				return bar.GetHashCode() + ShowBar.GetHashCode();
			}

			public override bool Equals(object obj)
			{
				var other = obj as BarInfo;
				return other != null && other.bar == this.bar && other.ShowBar == this.ShowBar;
			}
		}

		private static int count;
		private static bool isWindowsTaskbarShown;
		private static NativeWindowWithHeight shownTopNativeWindow;
		private static NativeWindowWithHeight shownBottomNativeWindow;
		private static IEnumerable<BarInfo> shownTopBars;
		private static IEnumerable<BarInfo> shownBottomBars;

		private int floatingWindowsCount;
		private int windowsShownInTabsCount;

		private readonly LinkedList<Window> windows; // all windows, owner window, sorted in Z-order, topmost window first
		private readonly LinkedList<Window> managedWindows; // windows.Where(w => !w.isFloating && !w.isMinimized), owned windows, not sorted
		private readonly LinkedList<Window> sharedWindows; // windows.Where(w => w.shared), not sorted
		private readonly LinkedList<Window> removedSharedWindows; // windows that need to be Initialized but then removed from shared
		internal bool hasChanges;

		#region Events

		public delegate void WorkspaceApplicationAddedEventHandler(Workspace workspace, Window window);
		public static event WorkspaceApplicationAddedEventHandler WorkspaceApplicationAdded;

		public delegate void WorkspaceApplicationRemovedEventHandler(Workspace workspace, Window window);
		public static event WorkspaceApplicationRemovedEventHandler WorkspaceApplicationRemoved;

		public delegate void WorkspaceApplicationMinimizedEventHandler(Workspace workspace, Window window);
		public static event WorkspaceApplicationMinimizedEventHandler WorkspaceApplicationMinimized;

		public delegate void WorkspaceApplicationRestoredEventHandler(Workspace workspace, Window window);
		public static event WorkspaceApplicationRestoredEventHandler WorkspaceApplicationRestored;

		public delegate void WorkspaceChangedFromEventHandler(Workspace workspace);
		public static event WorkspaceChangedFromEventHandler WorkspaceChangedFrom;

		public delegate void WorkspaceChangedToEventHandler(Workspace workspace);
		public static event WorkspaceChangedToEventHandler WorkspaceChangedTo;

		public delegate void WorkspaceLayoutChangedEventHandler(Workspace workspace, ILayout oldLayout);
		public static event WorkspaceLayoutChangedEventHandler WorkspaceLayoutChanged;

		public delegate void WindowActivatedEventHandler(IntPtr hWnd);
		public static event WindowActivatedEventHandler WindowActivatedEvent;

		private static void DoWorkspaceApplicationAdded(Workspace workspace, Window window)
		{
			if (WorkspaceApplicationAdded != null)
			{
				WorkspaceApplicationAdded(workspace, window);
			}
			Windawesome.DoLayoutUpdated();
		}

		private static void DoWorkspaceApplicationRemoved(Workspace workspace, Window window)
		{
			if (WorkspaceApplicationRemoved != null)
			{
				WorkspaceApplicationRemoved(workspace, window);
			}
			Windawesome.DoLayoutUpdated();
		}

		private static void DoWorkspaceApplicationMinimized(Workspace workspace, Window window)
		{
			if (WorkspaceApplicationMinimized != null)
			{
				WorkspaceApplicationMinimized(workspace, window);
			}
		}

		private static void DoWorkspaceApplicationRestored(Workspace workspace, Window window)
		{
			if (WorkspaceApplicationRestored != null)
			{
				WorkspaceApplicationRestored(workspace, window);
			}
		}

		private static void DoWorkspaceChangedFrom(Workspace workspace)
		{
			if (WorkspaceChangedFrom != null)
			{
				WorkspaceChangedFrom(workspace);
			}
		}

		private static void DoWorkspaceChangedTo(Workspace workspace)
		{
			if (WorkspaceChangedTo != null)
			{
				WorkspaceChangedTo(workspace);
			}
			Windawesome.DoLayoutUpdated();
		}

		private static void DoWorkspaceLayoutChanged(Workspace workspace, ILayout oldLayout)
		{
			if (WorkspaceLayoutChanged != null)
			{
				WorkspaceLayoutChanged(workspace, oldLayout);
			}
			Windawesome.DoLayoutUpdated();
		}

		private static void DoWindowActivated(IntPtr hWnd)
		{
			if (WindowActivatedEvent != null)
			{
				WindowActivatedEvent(hWnd);
			}
		}

		#endregion






		private static uint messageNumber; // TODO: this is not right
		private static void RegisterBar(IBar bar)
		{
			messageNumber = NativeMethods.RegisterWindowMessage("AppBarMessage" + bar.Handle);

			var appBarData = new NativeMethods.APPBARDATA
				{
					hWnd = bar.Handle,
					uCallbackMessage = messageNumber
				};

			NativeMethods.SHAppBarMessage(NativeMethods.ABM.ABM_NEW, ref appBarData);

			registeredBars.Add(bar);
		}

		private static void UnregisterBar(IBar bar)
		{
			var appBarData = new NativeMethods.APPBARDATA
				{
					hWnd = bar.Handle
				};

			NativeMethods.SHAppBarMessage(NativeMethods.ABM.ABM_REMOVE, ref appBarData);

			registeredBars.Remove(bar);
		}

		private static void AppBarFormSetPos(NativeWindowWithHeight nativeWindow, bool topBar)
		{
			var appBarData = new NativeMethods.APPBARDATA
				{
					hWnd = nativeWindow.Handle,
					uEdge = topBar ? NativeMethods.ABE.ABE_TOP : NativeMethods.ABE.ABE_BOTTOM
				};

			var workingArea = SystemInformation.WorkingArea;
			appBarData.rc.left = workingArea.Left;
			appBarData.rc.right = workingArea.Right;
			if (topBar)
			{
				appBarData.rc.top = workingArea.Top;
				appBarData.rc.bottom = appBarData.rc.top + nativeWindow.Height;
			}
			else
			{
				appBarData.rc.bottom = workingArea.Bottom;
				appBarData.rc.top = appBarData.rc.bottom - nativeWindow.Height;
			}

			NativeMethods.SHAppBarMessage(NativeMethods.ABM.ABM_SETPOS, ref appBarData);
		}

		private static void AppBarFormHide(NativeWindowWithHeight nativeWindow)
		{
			var appBarData = new NativeMethods.APPBARDATA();
			appBarData.hWnd = nativeWindow.Handle;
			appBarData.uEdge = NativeMethods.ABE.ABE_TOP;

			appBarData.rc.left = appBarData.rc.right = appBarData.rc.top = appBarData.rc.bottom = 0;

			NativeMethods.SHAppBarMessage(NativeMethods.ABM.ABM_SETPOS, ref appBarData);
		}











		public string LayoutSymbol
		{
			get
			{
				return Layout.LayoutSymbol(windowsShownInTabsCount);
			}
		}

		static Workspace()
		{
			taskbarHandle = NativeMethods.FindWindow("Shell_TrayWnd", null);
			if (Windawesome.isAtLeastVista)
			{
				startButtonHandle = NativeMethods.FindWindow("Button", "Start");
			}

			shownTopBars = new BarInfo[0];
			shownBottomBars = new BarInfo[0];
		}

		public Workspace(ILayout layout, IEnumerable<BarInfo> barsAtTop = null, IEnumerable<BarInfo> barsAtBottom = null, string name = "", bool showWindowsTaskbar = false,
			bool repositionOnSwitchedTo = false)
		{
			windows = new LinkedList<Window>();
			managedWindows = new LinkedList<Window>();
			sharedWindows = new LinkedList<Window>();
			removedSharedWindows = new LinkedList<Window>();

			this.id = ++count;
			this.Layout = layout;
			this.barsAtTop = barsAtTop != null ? barsAtTop.ToArray() : new BarInfo[] { };
			this.barsAtBottom = barsAtBottom != null ? barsAtBottom.ToArray() : new BarInfo[] { };
			this.name = name;
			this.ShowWindowsTaskbar = showWindowsTaskbar;
			this.repositionOnSwitchedTo = repositionOnSwitchedTo;

			this.barsAtTop.Select(bi => bi.bar).Unless(registeredBars.Contains).ForEach(RegisterBar);
			this.barsAtBottom.Select(bi => bi.bar).Unless(registeredBars.Contains).ForEach(RegisterBar);
		}

		internal void Dispose()
		{
			barsAtTop.Select(bi => bi.bar).Where(registeredBars.Contains).ForEach(UnregisterBar);
			barsAtBottom.Select(bi => bi.bar).Where(registeredBars.Contains).ForEach(UnregisterBar);
		}

		public override int GetHashCode()
		{
			return this.id;
		}

		public override bool Equals(object obj)
		{
			var workspace = obj as Workspace;
			return workspace != null && workspace.id == this.id;
		}

		internal void SwitchTo()
		{
			// hides or shows the Windows taskbar
			if (this.ShowWindowsTaskbar != isWindowsTaskbarShown)
			{
				ShowHideWindowsTaskbar();
			}

			// hides the Bars for the old workspace and shows the new ones
			if (Windawesome.workspaceBarsEquivalentClasses[Windawesome.PreviousWorkspace - 1] != Windawesome.workspaceBarsEquivalentClasses[this.id - 1])
			{
				HideShowBars();
			}

			// sets the layout- and workspace-specific changes to the windows
			sharedWindows.ForEach(SetSharedWindowChanges);
			if (removedSharedWindows.Count > 0)
			{
				removedSharedWindows.ForEach(w => sharedWindows.Remove(w));
				removedSharedWindows.Clear();
			}

			if (hasChanges || repositionOnSwitchedTo)
			{
				// Repositions if there is/are new/deleted windows
				Reposition();
				hasChanges = false;
			}

			IsCurrentWorkspace = true;

			DoWorkspaceChangedTo(this);
		}

		internal void Unswitch()
		{
			sharedWindows.ForEach(window => window.SavePosition());

			IsCurrentWorkspace = false;

			DoWorkspaceChangedFrom(this);
		}

		private void SetSharedWindowChanges(Window window)
		{
			window.Initialize();
			if ((!hasChanges && !repositionOnSwitchedTo) || window.IsFloating || Layout.ShouldRestoreSharedWindowsPosition())
			{
				window.RestorePosition();
			}
		}

		public void Reposition()
		{
			Layout.Reposition(managedWindows);
		}

		internal void RevertToInitialValues()
		{
			if (!isWindowsTaskbarShown)
			{
				ShowWindowsTaskbar = !ShowWindowsTaskbar;
				ShowHideWindowsTaskbar();
			}
		}

		public void ChangeLayout(ILayout layout)
		{
			if (layout.LayoutName() != this.Layout.LayoutName())
			{
				var oldLayout = this.Layout;
				this.Layout = layout;
				Reposition();
				DoWorkspaceLayoutChanged(this, oldLayout);
			}
		}

		internal void ShowHideWindowsTaskbar()
		{
			var appBarData = new NativeMethods.APPBARDATA();
			var state = (NativeMethods.ABS) NativeMethods.SHAppBarMessage(NativeMethods.ABM.ABM_GETSTATE, ref appBarData);

			appBarData.hWnd = taskbarHandle;
			appBarData.lParam = (IntPtr) (ShowWindowsTaskbar ? state & ~NativeMethods.ABS.ABS_AUTOHIDE : state | NativeMethods.ABS.ABS_AUTOHIDE);
			NativeMethods.SHAppBarMessage(NativeMethods.ABM.ABM_SETSTATE, ref appBarData);

			var showHide = ShowWindowsTaskbar ? NativeMethods.SW.SW_SHOWNA : NativeMethods.SW.SW_HIDE;

			NativeMethods.EnableWindow(taskbarHandle, ShowWindowsTaskbar);
			NativeMethods.ShowWindowAsync(taskbarHandle, showHide);
			if (Windawesome.isAtLeastVista)
			{
				NativeMethods.ShowWindowAsync(startButtonHandle, showHide);
			}

			isWindowsTaskbarShown = ShowWindowsTaskbar;
		}

		private void HideShowBars()
		{
			HideShowBars(shownTopBars, barsAtTop, ref shownTopNativeWindow, appBarTopWindows[id - 1], true);
			HideShowBars(shownBottomBars, barsAtBottom, ref shownBottomNativeWindow, appBarBottomWindows[id - 1], false);

			shownTopBars = barsAtTop.Where(bi => bi.ShowBar);
			shownBottomBars = barsAtBottom.Where(bi => bi.ShowBar);
		}

		private void HideShowBars(IEnumerable<BarInfo> hideBars, IEnumerable<BarInfo> showBars, ref NativeWindowWithHeight hideForm, NativeWindowWithHeight showForm, bool topBars)
		{
			// this whole thing is so complicated as to avoid changing of the working area if the bars in the new workspace
			// take the same space as the one in the previous one
			var changedWorkingArea = false;

			if (hideForm != null)
			{
				if (showForm == null || hideForm.Height != showForm.Height)
				{
					AppBarFormHide(hideForm);
					if (showForm != null)
					{
						AppBarFormSetPos(showForm, topBars);
					}
					hideForm = showForm;
					changedWorkingArea = true;
				}
			}
			else if (showForm != null)
			{
				AppBarFormSetPos(showForm, topBars);
				hideForm = showForm;
				changedWorkingArea = true;
			}

			Rectangle workingArea = SystemInformation.WorkingArea;
			var currentY = topBars ?
				workingArea.Top - (showForm != null ? showForm.Height : 0) :
				workingArea.Bottom + (showForm != null ? showForm.Height : 0);
			foreach (var showBar in showBars.Where(bi => bi.ShowBar))
			{
				if (topBars)
				{
					showBar.bar.Location = new Point(workingArea.X, currentY);
					currentY += showBar.bar.GetBarHeight();
				}
				else
				{
					currentY -= showBar.bar.GetBarHeight();
					showBar.bar.Location = new Point(workingArea.X, currentY);
				}
				showBar.bar.Size = new Size(workingArea.Width, showBar.bar.GetBarHeight());
				showBar.bar.Show();
			}

			hideBars.Except(showBars).ForEach(bi => bi.bar.Hide());

			// when the working area changes, the Windows Taskbar is shown (at least if AutoHide is on)
			// on Windows XP SP3
			if (changedWorkingArea)
			{
				ShowHideWindowsTaskbar();
			}
		}

		internal void OnWorkingAreaReset(Rectangle newWorkingArea)
		{
			//shownBars = new BarInfo[0];
			//SetWorkingAreaAndBarPositions();
			ShowHideWindowsTaskbar();
			Reposition();
		}

		internal void OnScreenResolutionChanged(Rectangle newWorkingArea)
		{
			//this.bars.ForEach(bs =>
			//    bs.bar.Size = bs.barPosition.Size = new Size(workingArea.Width, bs.bar.GetBarHeight()));
			//this.bars.ForEach(bs =>
			//    bs.bar.Size = new Size(newWorkingArea.Width, bs.bar.GetBarHeight()));

			ShowHideWindowsTaskbar();
			Reposition();
		}

		internal void ToggleWindowsTaskbarVisibility()
		{
			ShowWindowsTaskbar = !ShowWindowsTaskbar;
			ShowHideWindowsTaskbar();
			Reposition();
		}

		internal void ToggleShowHideBar(IBar bar)
		{
			var barStruct = barsAtTop.FirstOrDefault(bs => bs.bar == bar) ?? barsAtBottom.FirstOrDefault(bs => bs.bar == bar);
			if (barStruct != null)
			{
				barStruct.ShowBar = !barStruct.ShowBar;

				HideShowBars();

				Reposition();
			}
		}

		internal void SetTopManagedWindowAsForeground()
		{
			var topmost = GetTopmostWindow();
			if (topmost != null)
			{
				Windawesome.ForceForegroundWindow(topmost);
			}
			else
			{
				Windawesome.ForceForegroundWindow(NativeMethods.GetDesktopWindow());
			}
		}

		internal void WindowMinimized(IntPtr hWnd)
		{
			var window = MoveWindowToBottom(hWnd);
			if (window != null)
			{
				window.DoForSelfOrOwned(w =>
					{
						if (!w.IsMinimized)
						{
							w.IsMinimized = true;
							if (managedWindows.Remove(w))
							{
								Layout.WindowMinimized(w, managedWindows);
							}
						}
					});

				window.IsMinimized = true;

				DoWorkspaceApplicationMinimized(this, window);
			}
		}

		internal void WindowRestored(IntPtr hWnd)
		{
			var window = MoveWindowToTop(hWnd);
			if (window != null)
			{
				window.DoForSelfOrOwned(w =>
					{
						if (w.IsMinimized)
						{
							w.IsMinimized = false;
							if (!w.IsFloating)
							{
								managedWindows.AddFirst(w);
								Layout.WindowRestored(w, managedWindows);
							}
						}
					});

				window.IsMinimized = false;

				DoWorkspaceApplicationRestored(this, window);
			}
		}

		public const int minimizeRestoreDelay = 200;
		internal void WindowActivated(IntPtr hWnd)
		{
			Window window;
			if (hWnd == IntPtr.Zero && windows.Count > 0)
			{
				window = windows.First.Value;
				if (!window.IsMinimized)
				{
					// sometimes Windows doesn't send a HSHELL_GETMINRECT message on minimize
					System.Threading.Thread.Sleep(minimizeRestoreDelay);
					if (NativeMethods.IsIconic(window.hWnd))
					{
						WindowMinimized(window.hWnd);
					}
				}
			}
			else if ((window = MoveWindowToTop(hWnd)) != null)
			{
				if (window.IsMinimized)
				{
					System.Threading.Thread.Sleep(minimizeRestoreDelay);
					if (!NativeMethods.IsIconic(hWnd))
					{
						// sometimes Windows doesn't send a HSHELL_GETMINRECT message on restore
						WindowRestored(hWnd);
						return ;
					}
				}
				else if (windows.Count > 1)
				{
					var secondZOrderWindow = windows.First.Next.Value;
					if (!secondZOrderWindow.IsMinimized)
					{
						// sometimes Windows doesn't send a HSHELL_GETMINRECT message on minimize
						System.Threading.Thread.Sleep(minimizeRestoreDelay);
						if (NativeMethods.IsIconic(secondZOrderWindow.hWnd))
						{
							WindowMinimized(secondZOrderWindow.hWnd);
						}
					}
				}
			}

			DoWindowActivated(hWnd);
		}

		internal void WindowCreated(Window window)
		{
			windows.AddFirst(window);
			if (window.WorkspacesCount > 1)
			{
				window.DoForSelfOrOwned(w => sharedWindows.AddFirst(w));
			}
			if (window.ShowInTabs)
			{
				windowsShownInTabsCount++;
			}
			if (IsCurrentWorkspace || window.WorkspacesCount == 1)
			{
				window.DoForSelfOrOwned(w => w.Initialize());
			}

			window.DoForSelfOrOwned(w =>
				{
					if (w.IsFloating)
					{
						floatingWindowsCount++;
					}
					else if (!w.IsMinimized)
					{
						managedWindows.AddFirst(w);
						Layout.WindowCreated(w, managedWindows, IsCurrentWorkspace);

						hasChanges |= !IsCurrentWorkspace;
					}
				});

			DoWorkspaceApplicationAdded(this, window);
		}

		internal void WindowDestroyed(Window window, bool setForeground = true)
		{
			windows.Remove(window);
			if (window.WorkspacesCount > 1)
			{
				window.DoForSelfOrOwned(w => sharedWindows.Remove(w));
			}
			if (window.ShowInTabs)
			{
				windowsShownInTabsCount--;
			}

			window.DoForSelfOrOwned(w =>
				{
					if (w.IsFloating)
					{
						floatingWindowsCount--;
					}
					else if (!w.IsMinimized)
					{
						managedWindows.Remove(w);
						Layout.WindowDestroyed(w, managedWindows, IsCurrentWorkspace);

						hasChanges |= !IsCurrentWorkspace;
					}
				});

			if (IsCurrentWorkspace && setForeground)
			{
				SetTopManagedWindowAsForeground(); // TODO: perhaps switch to the last window that was foreground?
			}

			DoWorkspaceApplicationRemoved(this, window);
		}

		public bool ContainsWindow(IntPtr hWnd)
		{
			return windows.Any(w => w.hWnd == hWnd);
		}

		public Window GetWindow(IntPtr hWnd)
		{
			return managedWindows.FirstOrDefault(w => w.hWnd == hWnd);
		}

		public int GetWindowsCount()
		{
			return windows.Count;
		}

		internal Window GetOwnermostWindow(IntPtr hWnd)
		{
			return windows.FirstOrDefault(w => w.hWnd == hWnd);
		}

		internal void ToggleWindowFloating(Window window)
		{
			if (window != null)
			{
				window.DoForSelfOrOwned(w =>
					{
						w.IsFloating = !w.IsFloating;
						if (w.IsFloating)
						{
							floatingWindowsCount++;
							managedWindows.Remove(w);
							Layout.WindowDestroyed(w, managedWindows, IsCurrentWorkspace);
						}
						else
						{
							floatingWindowsCount--;
							managedWindows.AddFirst(w);
							Layout.WindowCreated(w, managedWindows, IsCurrentWorkspace);
						}
					});
			}
		}

		internal static void ToggleShowHideWindowInTaskbar(Window window)
		{
			if (window != null)
			{
				window.ToggleShowHideInTaskbar();
			}
		}

		internal void ToggleShowHideWindowTitlebar(IntPtr hWnd)
		{
			var window = GetWindow(hWnd);
			if (window != null)
			{
				window.ToggleShowHideTitlebar();
				Layout.WindowTitlebarToggled(window, managedWindows);
			}
		}

		internal void ToggleShowHideWindowBorder(IntPtr hWnd)
		{
			var window = GetWindow(hWnd);
			if (window != null)
			{
				window.ToggleShowHideWindowBorder();
				Layout.WindowBorderToggled(window, managedWindows);
			}
		}

		internal void Initialize(bool startingWorkspace, Rectangle workingArea)
		{
			// I'm adding to the front of the list in WindowCreated, however EnumWindows enums
			// from the top of the Z-order to the bottom, so I need to reverse the list
			var newWindows = windows.ToArray();
			windows.Clear();
			newWindows.ForEach(w => windows.AddFirst(w));

			if (startingWorkspace)
			{
				isWindowsTaskbarShown = !ShowWindowsTaskbar;
				HideShowBars();
			}
			else
			{
				windows.ForEach(w => w.Hide());
			}
		}

		private LinkedListNode<Window> GetWindowNode(IntPtr hWnd)
		{
			for (var node = windows.First; node != null; node = node.Next)
			{
				if (node.Value.hWnd == hWnd)
				{
					return node;
				}
			}

			return null;
		}

		private Window MoveWindowToTop(IntPtr hWnd)
		{
			var node = GetWindowNode(hWnd);

			if (node != null)
			{
				if (node != windows.First)
				{
					// adds the window to the front of the list, i.e. the top of the Z order
					windows.Remove(node);
					windows.AddFirst(node);
				}

				return node.Value;
			}

			return null;
		}

		private Window MoveWindowToBottom(IntPtr hWnd)
		{
			var node = GetWindowNode(hWnd);

			if (node != null)
			{
				if (node != windows.First)
				{
					// adds the window to the back of the list, i.e. the bottom of the Z order
					windows.Remove(node);
					windows.AddLast(node);
				}

				return node.Value;
			}

			return null;
		}

		public Window GetTopmostWindow()
		{
			var window = windows.FirstOrDefault();
			return (window != null && !window.IsMinimized) ? window : null;
		}

		internal void AddToSharedWindows(Window window)
		{
			window.DoForSelfOrOwned(w => sharedWindows.AddFirst(w));
		}

		internal void AddToRemovedSharedWindows(Window window)
		{
			window.DoForSelfOrOwned(w => removedSharedWindows.AddFirst(w));
		}

		internal IEnumerable<Window> GetWindows()
		{
			return windows;
		}
	}

	public class Window
	{
		public readonly IntPtr hWnd;
		public bool IsFloating { get; internal set; }
		public bool ShowInTabs { get; private set; }
		public State Titlebar { get; internal set; }
		public State InTaskbar { get; internal set; }
		public State WindowBorders { get; internal set; }
		public int WorkspacesCount { get; internal set; } // if > 1 window is shared between two or more workspaces
		public bool IsMinimized { get; internal set; }
		public string DisplayName { get; internal set; }
		public readonly string className;
		public readonly string processName;
		public readonly bool is64BitProcess;
		public readonly bool redrawOnShow;
		public readonly bool activateLastActivePopup;
		public readonly bool hideOwnedPopups;
		public readonly OnWindowShownAction onHiddenWindowShownAction;

		internal readonly LinkedList<Window> ownedWindows;

		private readonly NativeMethods.WS titlebarStyle;

		private readonly NativeMethods.WS borderStyle;
		private readonly NativeMethods.WS_EX borderExStyle;

		private NativeMethods.WINDOWPLACEMENT windowPlacement;
		private readonly NativeMethods.WINDOWPLACEMENT originalWindowPlacement;

		internal Window(IntPtr hWnd, string className, string displayName, string processName, int workspacesCount, bool is64BitProcess,
			NativeMethods.WS originalStyle, NativeMethods.WS_EX originalExStyle, LinkedList<Window> ownedWindows, ProgramRule.Rule rule, ProgramRule programRule)
		{
			this.hWnd = hWnd;
			IsFloating = rule.isFloating;
			ShowInTabs = rule.showInTabs;
			Titlebar = rule.titlebar;
			InTaskbar = rule.inTaskbar;
			WindowBorders = rule.windowBorders;
			this.WorkspacesCount = workspacesCount;
			this.IsMinimized = NativeMethods.IsIconic(hWnd);
			this.DisplayName = displayName;
			this.className = className;
			this.processName = processName;
			this.is64BitProcess = is64BitProcess;
			redrawOnShow = rule.redrawOnShow;
			activateLastActivePopup = rule.activateLastActivePopup;
			hideOwnedPopups = programRule.hideOwnedPopups;
			onHiddenWindowShownAction = programRule.onHiddenWindowShownAction;

			this.ownedWindows = ownedWindows;

			titlebarStyle = 0;
			titlebarStyle |= originalStyle & NativeMethods.WS.WS_CAPTION;
			titlebarStyle |= originalStyle & NativeMethods.WS.WS_MINIMIZEBOX;
			titlebarStyle |= originalStyle & NativeMethods.WS.WS_MAXIMIZEBOX;
			titlebarStyle |= originalStyle & NativeMethods.WS.WS_SYSMENU;

			borderStyle = 0;
			borderStyle |= originalStyle & NativeMethods.WS.WS_SIZEBOX;

			borderExStyle = 0;
			borderExStyle |= originalExStyle & NativeMethods.WS_EX.WS_EX_DLGMODALFRAME;
			borderExStyle |= originalExStyle & NativeMethods.WS_EX.WS_EX_CLIENTEDGE;
			borderExStyle |= originalExStyle & NativeMethods.WS_EX.WS_EX_STATICEDGE;
			borderExStyle |= originalExStyle & NativeMethods.WS_EX.WS_EX_WINDOWEDGE;

			windowPlacement = NativeMethods.WINDOWPLACEMENT.Default;
			SavePosition();
			originalWindowPlacement = windowPlacement;
		}

		internal Window(Window window)
		{
			hWnd = window.hWnd;
			this.IsFloating = window.IsFloating;
			this.ShowInTabs = window.ShowInTabs;
			this.Titlebar = window.Titlebar;
			this.InTaskbar = window.InTaskbar;
			this.WindowBorders = window.WindowBorders;
			this.WorkspacesCount = window.WorkspacesCount;
			IsMinimized = window.IsMinimized;
			this.DisplayName = window.DisplayName;
			className = window.className;
			processName = window.processName;
			is64BitProcess = window.is64BitProcess;
			redrawOnShow = window.redrawOnShow;
			activateLastActivePopup = window.activateLastActivePopup;
			hideOwnedPopups = window.hideOwnedPopups;
			onHiddenWindowShownAction = window.onHiddenWindowShownAction;

			if (window.ownedWindows != null)
			{
				this.ownedWindows = new LinkedList<Window>(window.ownedWindows.Select(w => new Window(w)));
			}

			titlebarStyle = window.titlebarStyle;

			borderStyle = window.borderStyle;
			borderExStyle = window.borderExStyle;

			windowPlacement = window.windowPlacement;
			originalWindowPlacement = window.originalWindowPlacement;
		}

		internal void Initialize()
		{
			var style = NativeMethods.GetWindowStyleLongPtr(hWnd);
			var exStyle = NativeMethods.GetWindowExStyleLongPtr(hWnd);
			var prevStyle = style;
			var prevExStyle = exStyle;

			switch (this.InTaskbar)
			{
				case State.SHOWN:
					exStyle = (exStyle | NativeMethods.WS_EX.WS_EX_APPWINDOW) & ~NativeMethods.WS_EX.WS_EX_TOOLWINDOW;
					break;
				case State.HIDDEN:
					exStyle = (exStyle & ~NativeMethods.WS_EX.WS_EX_APPWINDOW) | NativeMethods.WS_EX.WS_EX_TOOLWINDOW;
					break;
			}
			switch (this.Titlebar)
			{
				case State.SHOWN:
					style |= titlebarStyle;
					break;
				case State.HIDDEN:
					style &= ~titlebarStyle;
					break;
			}
			switch (this.WindowBorders)
			{
				case State.SHOWN:
					style	|= borderStyle;
					exStyle |= borderExStyle;
					break;
				case State.HIDDEN:
					style	&= ~borderStyle;
					exStyle &= ~borderExStyle;
					break;
			}

			var redraw = false;
			if (style != prevStyle)
			{
				NativeMethods.SetWindowStyleLongPtr(hWnd, style);
				redraw = true;
			}
			if (exStyle != prevExStyle)
			{
				NativeMethods.SetWindowExStyleLongPtr(hWnd, exStyle);
				redraw = true;
			}

			if (redraw)
			{
				Redraw();
			}
		}

		internal void ToggleShowHideInTaskbar()
		{
			this.InTaskbar = (State) (((int) this.InTaskbar + 1) % 2);
			Initialize();
		}

		internal void ToggleShowHideTitlebar()
		{
			this.Titlebar = (State) (((int) this.Titlebar + 1) % 2);
			Initialize();
		}

		internal void ToggleShowHideWindowBorder()
		{
			this.WindowBorders = (State) (((int) this.WindowBorders + 1) % 2);
			Initialize();
		}

		internal void Redraw()
		{
			// this whole thing is a hack but I've found no other way to make it work (and I've tried
			// a zillion things). Resizing seems to do the best job.
			NativeMethods.RECT rect;
			NativeMethods.GetWindowRect(hWnd, out rect);
			NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top - 1,
				NativeMethods.SWP.SWP_ASYNCWINDOWPOS | NativeMethods.SWP.SWP_FRAMECHANGED | NativeMethods.SWP.SWP_NOMOVE |
				NativeMethods.SWP.SWP_NOZORDER | NativeMethods.SWP.SWP_NOACTIVATE |
				NativeMethods.SWP.SWP_NOOWNERZORDER | NativeMethods.SWP.SWP_NOCOPYBITS);
			NativeMethods.SetWindowPos(hWnd, IntPtr.Zero, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top,
				NativeMethods.SWP.SWP_ASYNCWINDOWPOS | NativeMethods.SWP.SWP_FRAMECHANGED | NativeMethods.SWP.SWP_NOMOVE |
				NativeMethods.SWP.SWP_NOZORDER | NativeMethods.SWP.SWP_NOACTIVATE |
				NativeMethods.SWP.SWP_NOOWNERZORDER | NativeMethods.SWP.SWP_NOCOPYBITS);

			NativeMethods.RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero,
				NativeMethods.RedrawWindowFlags.RDW_ALLCHILDREN |
				NativeMethods.RedrawWindowFlags.RDW_ERASE |
				NativeMethods.RedrawWindowFlags.RDW_INVALIDATE);
		}

		internal void SavePosition()
		{
			NativeMethods.GetWindowPlacement(hWnd, ref windowPlacement);
		}

		internal void RestorePosition()
		{
			switch (windowPlacement.ShowCmd)
			{
				case NativeMethods.SW.SW_SHOWNORMAL:
					windowPlacement.ShowCmd = NativeMethods.SW.SW_SHOWNOACTIVATE;
					break;
				case NativeMethods.SW.SW_SHOW:
					windowPlacement.ShowCmd = NativeMethods.SW.SW_SHOWNA;
					break;
				case NativeMethods.SW.SW_SHOWMINIMIZED:
					windowPlacement.ShowCmd = NativeMethods.SW.SW_SHOWMINNOACTIVE;
					break;
			}
			windowPlacement.Flags |= NativeMethods.WindowPlacementFlags.WPF_ASYNCWINDOWPLACEMENT;
			NativeMethods.SetWindowPlacement(hWnd, ref windowPlacement);
		}

		internal void ShowPopupsAndRedraw()
		{
			if (hideOwnedPopups)
			{
				NativeMethods.ShowOwnedPopups(hWnd, true);
			}

			if (redrawOnShow)
			{
				Redraw();
			}
		}

		internal void HidePopups()
		{
			if (hideOwnedPopups)
			{
				NativeMethods.ShowOwnedPopups(hWnd, false);
			}
		}

		internal void Hide()
		{
			HidePopups();
			NativeMethods.ShowWindowAsync(hWnd, NativeMethods.SW.SW_HIDE);
		}

		internal void DoForSelfOrOwned(Action<Window> action)
		{
			if (ownedWindows != null)
			{
				ownedWindows.ForEach(action);
			}
			else
			{
				action(this);
			}
		}

		internal void RevertToInitialValues()
		{
			if (this.Titlebar != State.AS_IS)
			{
				this.Titlebar = State.SHOWN;
			}
			if (this.InTaskbar != State.AS_IS)
			{
				this.InTaskbar = State.SHOWN;
			}
			if (this.WindowBorders != State.AS_IS)
			{
				this.WindowBorders = State.SHOWN;
			}
			Initialize();

			windowPlacement = originalWindowPlacement;
			RestorePosition();
		}

		public override int GetHashCode()
		{
			return hWnd.GetHashCode();
		}

		public override bool Equals(object obj)
		{
			var window = obj as Window;
			return window != null && window.hWnd == hWnd;
		}
	}
}
