﻿using System;
using System.IO;

namespace Windawesome
{
	public class LoggerPlugin : IPlugin
	{
		private readonly bool logRuleMatching;
		private readonly bool logCreation;
		private readonly bool logDeletion;
		private readonly bool logWorkspaceSwitching;
		private readonly bool logWindowMinimization;
		private readonly bool logWindowRestoration;
		private readonly bool logActivation;
		private readonly StreamWriter writer;
		private Windawesome windawesome;

		public LoggerPlugin(string filename = "logplugin.txt", bool logRuleMatching = true, bool logCreation = false, bool logDeletion = false,
			bool logWorkspaceSwitching = false, bool logWindowMinimization = false, bool logWindowRestoration = false,
			bool logActivation = false)
		{
			this.logRuleMatching = logRuleMatching;
			this.logCreation = logCreation;
			this.logDeletion = logDeletion;
			this.logWorkspaceSwitching = logWorkspaceSwitching;
			this.logWindowMinimization = logWindowMinimization;
			this.logWindowRestoration = logWindowRestoration;
			this.logActivation = logActivation;
			writer = new StreamWriter(filename, true);
		}

		private void OnProgramRuleMatched(ProgramRule programRule, IntPtr hWnd, string className, string displayName, string processName, NativeMethods.WS style, NativeMethods.WS_EX exStyle)
		{
			if (programRule != null)
			{
				writer.WriteLine("MATCHED - class '{0}'; caption '{1}'; processName '{2}';",
					className, displayName, processName);
				writer.WriteLine("\tAGAINST RULE WITH class '{0}'; caption '{1}'; process name '{2}';",
					programRule.className, programRule.displayName, programRule.processName);
				writer.WriteLine("\tstyle contains: '{0}'; style not contains '{1}'; ex style contains '{2}'; ex style not contains '{3}'; is managed '{4}'",
					programRule.styleContains, programRule.styleNotContains, programRule.exStyleContains, programRule.exStyleNotContains, programRule.isManaged);
			}
			else
			{
				writer.WriteLine("COULD NOT MATCH - class '{0}'; caption '{1}'; processName '{2}' AGAINST ANY RULE",
					className, displayName, processName);
			}
		}

		private void OnWorkspaceApplicationAdded(Workspace workspace, Window window)
		{
			writer.WriteLine("ADDED - class '{0}'; caption '{1}'; workspace '{2}'",
				window.className, window.DisplayName, workspace.id);
		}

		private void OnWorkspaceApplicationRemoved(Workspace workspace, Window window)
		{
			writer.WriteLine("REMOVED - class '{0}'; caption '{1}'; workspace '{2}'",
				window.className, window.DisplayName, workspace.id);
		}

		private void OnWorkspaceApplicationMinimized(Workspace workspace, Window window)
		{
			writer.WriteLine("MINIMIZED - class '{0}'; caption '{1}'; workspace '{2}'",
				window.className, window.DisplayName, workspace.id);
		}

		private void OnWorkspaceApplicationRestored(Workspace workspace, Window window)
		{
			writer.WriteLine("RESTORED - class '{0}'; caption '{1}'; workspace '{2}'",
				window.className, window.DisplayName, workspace.id);
		}

		private void OnWorkspaceShown(Workspace workspace)
		{
			writer.WriteLine("Workspace '{0}' shown", workspace.id);
		}

		private void OnWorkspaceHidden(Workspace workspace)
		{
			writer.WriteLine("Workspace '{0}' hidden", workspace.id);
		}

		private void OnWorkspaceActivated(Workspace workspace)
		{
			writer.WriteLine("Workspace '{0}' activated", workspace.id);
		}

		private void OnWorkspaceDeactivated(Workspace workspace)
		{
			writer.WriteLine("Workspace '{0}' deactivated", workspace.id);
		}

		private void OnWindowActivatedEvent(IntPtr hWnd)
		{
			var window = windawesome.CurrentWorkspace.GetWindow(hWnd);
			if (window != null)
			{
				writer.WriteLine("ACTIVATED - class '{0}'; caption '{1}'; workspace '{2}'",
					window.className, window.DisplayName, windawesome.CurrentWorkspace.id);
			}
			else
			{
				writer.WriteLine("ACTIVATED - HWND '{0}'; caption '{1}'; workspace '{2}'",
					hWnd, NativeMethods.GetText(hWnd), windawesome.CurrentWorkspace.id);
			}
		}

		#region IPlugin Members

		void IPlugin.InitializePlugin(Windawesome windawesome)
		{
			this.windawesome = windawesome;

			if (logRuleMatching)
			{
				Windawesome.ProgramRuleMatched += OnProgramRuleMatched;
			}
			if (logCreation)
			{
				Workspace.WorkspaceApplicationAdded += OnWorkspaceApplicationAdded;
			}
			if (logDeletion)
			{
				Workspace.WorkspaceApplicationRemoved += OnWorkspaceApplicationRemoved;
			}
			if (logWorkspaceSwitching)
			{
				Workspace.WorkspaceShown += OnWorkspaceShown;
				Workspace.WorkspaceHidden += OnWorkspaceHidden;
				Workspace.WorkspaceActivated += OnWorkspaceActivated;
				Workspace.WorkspaceDeactivated += OnWorkspaceDeactivated;
			}
			if (logWindowMinimization)
			{
				Workspace.WorkspaceApplicationMinimized += OnWorkspaceApplicationMinimized;
			}
			if (logWindowRestoration)
			{
				Workspace.WorkspaceApplicationRestored += OnWorkspaceApplicationRestored;
			}
			if (logActivation)
			{
				Workspace.WindowActivatedEvent += OnWindowActivatedEvent;
			}
		}

		void IPlugin.Dispose()
		{
			writer.WriteLine("==========================================");
			writer.Close();
		}

		#endregion
	}
}
