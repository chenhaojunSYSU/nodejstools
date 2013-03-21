﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.PythonTools;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Microsoft.NodejsTools.Profiling {
    /// <summary>
    /// This is the class that implements the package exposed by this assembly.
    ///
    /// The minimum requirement for a class to be considered a valid package for Visual Studio
    /// is to implement the IVsPackage interface and register itself with the shell.
    /// This package uses the helper classes defined inside the Managed Package Framework (MPF)
    /// to do it: it derives from the Package class that provides the implementation of the 
    /// IVsPackage interface and uses the registration attributes defined in the framework to 
    /// register itself and its components with the shell.
    /// </summary>
    // This attribute tells the PkgDef creation utility (CreatePkgDef.exe) that this class is
    // a package.
    [PackageRegistration(UseManagedResourcesOnly = true)]
    [Description("Node.js Tools Profiling Package")]
    // This attribute is used to register the informations needed to show the this package
    // in the Help/About dialog of Visual Studio.
    [InstalledProductRegistration("#110", "#112", "0.5", IconResourceID = 400)]
    // This attribute is needed to let the shell know that this package exposes some menus.
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(GuidList.guidNodeProfilingPkgString)]
    // set the window to dock where Toolbox/Performance Explorer dock by default
    [ProvideToolWindow(typeof(PerfToolWindow), Orientation = ToolWindowOrientation.Left, Style = VsDockStyle.Tabbed, Window = EnvDTE.Constants.vsWindowKindToolbox)]
    [ProvideFileFilterAttribute("{9C34161A-379E-4933-A0DC-871FE64D34F1}", "/1", "Node.js Performance Session (*" + PerfFileType + ");*" + PerfFileType, 100)]
    [ProvideEditorExtension(typeof(ProfilingSessionEditorFactory), ".njsperf", 50,
          ProjectGuid = "{9C34161A-379E-4933-A0DC-871FE64D34F1}",
          NameResourceID = 105,         
          DefaultName = "NodejsPerfSession")]
    [ProvideAutomationObject("NodeProfiling")]
    sealed class NodeProfilingPackage : Package {
        internal static NodeProfilingPackage Instance;
        private static ProfiledProcess _profilingProcess;   // process currently being profiled
        internal static string NodeProjectGuid = "{3AF33F2E-1136-4D97-BBB7-1795711AC8B8}";
        internal const string PerformanceFileFilter = "Performance Report Files|*.vspx;*.vsps";
        private AutomationProfiling _profilingAutomation;
        private static OleMenuCommand _stopCommand, _startCommand;
        internal const string PerfFileType = ".njsperf";

        /// <summary>
        /// Default constructor of the package.
        /// Inside this method you can place any initialization code that does not require 
        /// any Visual Studio service because at this point the package object is created but 
        /// not sited yet inside Visual Studio environment. The place to do all the other 
        /// initialization is the Initialize method.
        /// </summary>
        public NodeProfilingPackage() {
            Instance = this;
        }

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initilaization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize() {
            Trace.WriteLine(string.Format(CultureInfo.CurrentCulture, "Entering Initialize() of: {0}", this.ToString()));
            base.Initialize();

            // Add our command handlers for menu (commands must exist in the .vsct file)
            OleMenuCommandService mcs = GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (null != mcs) {
                // Create the command for the menu item.
                CommandID menuCommandID = new CommandID(GuidList.guidNodeProfilingCmdSet, (int)PkgCmdIDList.cmdidStartNodeProfiling);
                MenuCommand menuItem = new MenuCommand(StartProfilingWizard, menuCommandID);
                mcs.AddCommand(menuItem);

                // Create the command for the menu item.
                menuCommandID = new CommandID(GuidList.guidNodeProfilingCmdSet, (int)PkgCmdIDList.cmdidPerfExplorer);
                var oleMenuItem = new OleMenuCommand(ShowPeformanceExplorer, menuCommandID);
                oleMenuItem.BeforeQueryStatus += ShowPerfQueryStatus;
                mcs.AddCommand(oleMenuItem);

                menuCommandID = new CommandID(GuidList.guidNodeProfilingCmdSet, (int)PkgCmdIDList.cmdidAddPerfSession);
                menuItem = new MenuCommand(AddPerformanceSession, menuCommandID);
                mcs.AddCommand(menuItem);

                menuCommandID = new CommandID(GuidList.guidNodeProfilingCmdSet, (int)PkgCmdIDList.cmdidStartProfiling);
                oleMenuItem = _startCommand = new OleMenuCommand(StartProfiling, menuCommandID);
                oleMenuItem.BeforeQueryStatus += IsProfilingActive;
                mcs.AddCommand(oleMenuItem);

                menuCommandID = new CommandID(GuidList.guidNodeProfilingCmdSet, (int)PkgCmdIDList.cmdidStopProfiling);
                _stopCommand = oleMenuItem = new OleMenuCommand(StopProfiling, menuCommandID);
                oleMenuItem.BeforeQueryStatus += IsProfilingInactive;                
            
                mcs.AddCommand(oleMenuItem);
            }

            //Create Editor Factory. Note that the base Package class will call Dispose on it.
            base.RegisterEditorFactory(new ProfilingSessionEditorFactory(this));
        }

        protected override object GetAutomationObject(string name) {
            if (name == "NodeProfiling") {
                if (_profilingAutomation == null) {
                    var pane = (PerfToolWindow)this.FindToolWindow(typeof(PerfToolWindow), 0, true);
                    _profilingAutomation  = new AutomationProfiling(pane.Sessions);
                }
                return _profilingAutomation;
            }

            return base.GetAutomationObject(name);
        }

        /// <summary>
        /// This function is the callback used to execute a command when the a menu item is clicked.
        /// See the Initialize method to see how the menu item is associated to this function using
        /// the OleMenuCommandService service and the MenuCommand class.
        /// </summary>
        private void StartProfilingWizard(object sender, EventArgs e) {
            var targetView = new ProfilingTargetView();
            var dialog = new LaunchProfiling(targetView);
            var res = dialog.ShowModal() ?? false;
            if (res  && targetView.IsValid) {
                var target = targetView.GetTarget();
                if (target != null) {
                    ProfileTarget(target);
                }
            }
        }

        internal SessionNode ProfileTarget(ProfilingTarget target, bool openReport = true) {
            bool save;
            string name = target.GetProfilingName(out save);
            var session = ShowPerformanceExplorer().Sessions.AddTarget(target, name, save);

            StartProfiling(target, session, openReport);
            return session;
        }

        internal void StartProfiling(ProfilingTarget target, SessionNode session, bool openReport = true) {
            if (target.ProjectTarget != null) {
                ProfileProjectTarget(session, target.ProjectTarget, openReport);
            } else if (target.StandaloneTarget != null) {
                ProfileStandaloneTarget(session, target.StandaloneTarget, openReport);
            } else {
                if (MessageBox.Show("Profiling session is not configured - would you like to configure now and then launch?", "No Profiling Target", MessageBoxButton.YesNo) == MessageBoxResult.Yes) {
                    var newTarget = session.OpenTargetProperties();
                    if (newTarget != null && (newTarget.ProjectTarget != null || newTarget.StandaloneTarget != null)) {
                        StartProfiling(newTarget, session, openReport);
                    }
                }
            }
        }

        private void ProfileProjectTarget(SessionNode session, ProjectTarget projectTarget, bool openReport) {
            var targetGuid = projectTarget.TargetProject;

            var dte = (EnvDTE.DTE)GetGlobalService(typeof(EnvDTE.DTE));
            EnvDTE.Project projectToProfile = null;
            foreach (EnvDTE.Project project in dte.Solution.Projects) {
                var kind = project.Kind;
                
                if (String.Equals(kind, NodeProfilingPackage.NodeProjectGuid, StringComparison.OrdinalIgnoreCase)) {
                    var guid = project.Properties.Item("Guid").Value as string;

                    Guid guidVal;
                    if (Guid.TryParse(guid, out guidVal) && guidVal == projectTarget.TargetProject) {
                        projectToProfile = project;
                        break;
                    }
                }
            }

            if (projectToProfile != null) {
                ProfileProject(session, projectToProfile, openReport);
            } else {
                MessageBox.Show("Project could not be found in current solution.", Resources.NodejsToolsForVS);
            }
        }

        internal static void ProfileProject(SessionNode session, EnvDTE.Project projectToProfile, bool openReport) {
            var args = (string)projectToProfile.Properties.Item("CommandLineArguments").Value;

            string interpreterPath = NodePackage.NodePath;

            string startupFile = (string)projectToProfile.Properties.Item("StartupFile").Value;
            if (String.IsNullOrEmpty(startupFile)) {
                MessageBox.Show("Project has no configured startup file, cannot start profiling.", Resources.NodejsToolsForVS);
                return;
            }

            string workingDir = projectToProfile.Properties.Item("WorkingDirectory").Value as string;
            if (String.IsNullOrEmpty(workingDir) || workingDir == ".") {
                workingDir = projectToProfile.Properties.Item("ProjectHome").Value as string;
                if (String.IsNullOrEmpty(workingDir)) {
                    workingDir = Path.GetDirectoryName(projectToProfile.FullName);
                }
            }

            RunProfiler(session, interpreterPath, startupFile, args, workingDir, null, openReport);
        }

        private static void ProfileStandaloneTarget(SessionNode session, StandaloneTarget runTarget, bool openReport) {
            RunProfiler(
                session, 
                runTarget.InterpreterPath, 
                runTarget.Script, 
                runTarget.Arguments, 
                runTarget.WorkingDirectory, 
                null, 
                openReport
            );
        }

        private static void RunProfiler(SessionNode session, string interpreter, string script, string arguments, string workingDir, Dictionary<string, string> env, bool openReport) {
            var arch = NativeMethods.GetBinaryType(interpreter);
            var process = new ProfiledProcess(interpreter, String.Format("\"{0}\" {1}", script, arguments), workingDir, env, arch);

            string baseName = Path.GetFileNameWithoutExtension(session.Filename);
            string date = DateTime.Now.ToString("yyyyMMdd");
            string outPath = Path.Combine(Path.GetTempPath(), baseName + "_" + date + ".vspx");

            int count = 1;
            while (File.Exists(outPath)) {
                outPath = Path.Combine(Path.GetTempPath(), baseName + "_" + date + "(" + count + ").vspx");
                count++;
            }
            
            process.ProcessExited += (sender, args) => {
                var dte = (EnvDTE.DTE)NodeProfilingPackage.GetGlobalService(typeof(EnvDTE.DTE));
                _profilingProcess = null;
                _stopCommand.Enabled = false;
                _startCommand.Enabled = true;
                if (openReport && File.Exists(outPath)) {                    
                    dte.ItemOperations.OpenFile(outPath);
                }
            };

            session.AddProfile(outPath);
            
            process.StartProfiling(outPath);
            _profilingProcess = process;
            _stopCommand.Enabled = true;
            _startCommand.Enabled = false;
        }

        /// <summary>
        /// This function is the callback used to execute a command when the a menu item is clicked.
        /// See the Initialize method to see how the menu item is associated to this function using
        /// the OleMenuCommandService service and the MenuCommand class.
        /// </summary>
        private void ShowPeformanceExplorer(object sender, EventArgs e) {
            ShowPerformanceExplorer();
        }

        internal PerfToolWindow ShowPerformanceExplorer() {
            var pane = this.FindToolWindow(typeof(PerfToolWindow), 0, true);
            if (pane == null) {
                throw new InvalidOperationException();
            }
            IVsWindowFrame frame = pane.Frame as IVsWindowFrame;
            if (frame == null) {
                throw new InvalidOperationException();
            }

            ErrorHandler.ThrowOnFailure(frame.Show());
            return pane as PerfToolWindow;
        }

        private void AddPerformanceSession(object sender, EventArgs e) {
            var dte = (EnvDTE.DTE)NodePackage.GetGlobalService(typeof(EnvDTE.DTE));
            string filename = "Performance" + PerfFileType;
            bool save = false;
            if (dte.Solution.IsOpen && !String.IsNullOrEmpty(dte.Solution.FullName)) {
                filename = Path.Combine(Path.GetDirectoryName(dte.Solution.FullName), filename);
                save = true;
            }
            ShowPerformanceExplorer().Sessions.AddTarget(new ProfilingTarget(), filename, save);
        }

        private void StartProfiling(object sender, EventArgs e) {
            ShowPerformanceExplorer().Sessions.StartProfiling();
        }

        private void StopProfiling(object sender, EventArgs e) {
            var process = _profilingProcess;
            if (process != null) {
                process.StopProfiling();
            }
        }

        private void IsProfilingActive(object sender, EventArgs args) {
            var oleMenu = sender as OleMenuCommand;

            if (_profilingProcess != null) {
                oleMenu.Enabled = false;
            } else {
                oleMenu.Enabled = true;
            }
        }

        private void IsProfilingInactive(object sender, EventArgs args) {
            var oleMenu = sender as OleMenuCommand;

            if (_profilingProcess != null) {
                oleMenu.Enabled = true;
            } else {
                oleMenu.Enabled = false;
            }
        }

        private void ShowPerfQueryStatus(object sender, EventArgs args) {
            var oleMenu = sender as OleMenuCommand;

            if (IsProfilingInstalled()) {
                oleMenu.Enabled = true;
                oleMenu.Visible = true;
            } else {
                oleMenu.Enabled = false;
                oleMenu.Visible = false;
            }
        }

        internal static bool IsProfilingInstalled() {
            IVsShell shell = (IVsShell)NodePackage.GetGlobalService(typeof(IVsShell));
            Guid perfGuid = GuidList.GuidPerfPkg;
            int installed;
            ErrorHandler.ThrowOnFailure(
                shell.IsPackageInstalled(ref perfGuid, out installed)
            );
            return installed != 0;
        }

        public bool IsProfiling {
            get {
                return _profilingProcess != null;
            }
        }
    }
}