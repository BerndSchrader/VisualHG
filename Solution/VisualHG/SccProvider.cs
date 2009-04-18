using System;
using System.IO;
using System.Resources;
using System.Diagnostics;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.ComponentModel.Design;
using Microsoft.Win32;

using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio;
using System.Runtime.Serialization.Formatters.Binary;
using MsVsShell = Microsoft.VisualStudio.Shell;
using ErrorHandler = Microsoft.VisualStudio.ErrorHandler;

namespace VisualHG
{
    /////////////////////////////////////////////////////////////////////////////
    // SccProvider
    [MsVsShell.DefaultRegistryRoot("Software\\Microsoft\\VisualStudio\\9.0Exp")]
    // Register the package to have information displayed in Help/About dialog box
    [MsVsShell.InstalledProductRegistration(false, "#100", "#101", "1.0.4", IconResourceID = CommandId.iiconProductIcon)]
    // Declare that resources for the package are to be found in the managed assembly resources, and not in a satellite dll
    [MsVsShell.PackageRegistration(UseManagedResourcesOnly = true)]
    // Register the resource ID of the CTMENU section (generated from compiling the VSCT file), so the IDE will know how to merge this package's menus with the rest of the IDE when "devenv /setup" is run
    // The menu resource ID needs to match the ResourceName number defined in the csproj project file in the VSCTCompile section
    // Everytime the version number changes VS will automatically update the menus on startup; if the version doesn't change, you will need to run manually "devenv /setup /rootsuffix:Exp" to see VSCT changes reflected in IDE
    [MsVsShell.ProvideMenuResource(1000, 1)]
    
    //TODO create meaningful page
    // Register the VisualHG options page visible as Tools/Options/SourceControl/VisualHG when the provider is active
    /// [MsVsShell.ProvideOptionPage(typeof(SccProviderOptions), "Source Control", "VisualHG", 106, 107, false)]
    /// [ProvideToolsOptionsPageVisibility("Source Control", "VisualHG", GuidList.ProviderGuid)]

    //TODO create meaningful page
    // Register the VisualHG tool window visible only when the provider is active
    //[MsVsShell.ProvideToolWindow(typeof(HGPendingChangesToolWindow))]
    /// [MsVsShell.ProvideToolWindowVisibility(typeof(HGPendingChangesToolWindow), GuidList.ProviderGuid)]

    // Register the source control provider's service (implementing IVsScciProvider interface)
    [MsVsShell.ProvideService(typeof(SccProviderService), ServiceName = "VisualHG")]
    // Register the source control provider to be visible in Tools/Options/SourceControl/Plugin dropdown selector
    [ProvideSourceControlProvider("VisualHG", "#100")]
    // Pre-load the package when the command UI context is asserted (the provider will be automatically loaded after restarting the shell if it was active last time the shell was shutdown)
    [MsVsShell.ProvideAutoLoad(GuidList.ProviderGuid)]
    // Register the key used for persisting solution properties, so the IDE will know to load the source control package when opening a controlled solution containing properties written by this package
    [ProvideSolutionProps(_strSolutionPersistanceKey)]
    [MsVsShell.ProvideLoadKey(PLK.MinEdition, PLK.PackageVersion, PLK.PakageName, PLK.CompanyName, 104)]
    // Declare the package guid
    [Guid(PLK.PackageGuid)]
    public sealed class SccProvider : MsVsShell.Package, IOleCommandTarget
    {
        // The service provider implemented by the package
        private SccProviderService sccService = null;
        // The name of this provider (to be written in solution and project files)
        // As a best practice, to be sure the provider has an unique name, a guid like the provider guid can be used as a part of the name
        private const string _strProviderName = "VisualHG:"+PLK.PackageGuid;
        // The name of the solution section used to persist provider options (should be unique)
        private const string _strSolutionPersistanceKey = "VisualHGProperties";
        // The name of the section in the solution user options file used to persist user-specific options (should be unique, shorter than 31 characters and without dots)
        private const string _strSolutionUserOptionsKey = "VisualHGSolution";
        // The names of the properties stored by the provider in the solution file
        private const string _strSolutionControlledProperty = "SolutionIsControlled";
        private const string _strSolutionBindingsProperty = "SolutionBindings";

        private SccOnIdleEvent _OnIdleEvent = new SccOnIdleEvent();

        public SccProvider()
        {
            Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture, "Entering constructor for: {0}", this.ToString()));
        }

        /////////////////////////////////////////////////////////////////////////////
        // SccProvider Package Implementation
        #region Package Members

        public new Object GetService(Type serviceType)
        {
            return base.GetService(serviceType);
        }

        protected override void Initialize()
        {
            Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture, "Entering Initialize() of: {0}", this.ToString()));
            base.Initialize();

            // Proffer the source control service implemented by the provider
            sccService = new SccProviderService(this);
            ((IServiceContainer)this).AddService(typeof(SccProviderService), sccService, true);

            // Add our command handlers for menu (commands must exist in the .vsct file)
            MsVsShell.OleMenuCommandService mcs = GetService(typeof(IMenuCommandService)) as MsVsShell.OleMenuCommandService;
            if (mcs != null)
            {
                // ToolWindow Command
                CommandID cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdViewToolWindow);
                MenuCommand menuCmd = new MenuCommand(new EventHandler(Exec_icmdViewToolWindow), cmd);
                mcs.AddCommand(menuCmd);

                // ToolWindow's ToolBar Command
                cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdToolWindowToolbarCommand);
                menuCmd = new MenuCommand(new EventHandler(Exec_icmdToolWindowToolbarCommand), cmd);
                mcs.AddCommand(menuCmd);

                // Source control menu commmads
                cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdHgStatus);
                menuCmd = new MenuCommand(new EventHandler(Exec_icmdHgStatus), cmd);
                mcs.AddCommand(menuCmd);

                cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdHgCommit);
                menuCmd = new MenuCommand(new EventHandler(Exec_icmdHgCommit), cmd);
                mcs.AddCommand(menuCmd);

                cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdHgHistory);
                menuCmd = new MenuCommand(new EventHandler(Exec_icmdHgHistory), cmd);
                mcs.AddCommand(menuCmd);

                cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdHgSynchronize);
                menuCmd = new MenuCommand(new EventHandler(Exec_icmdHgSynchronize), cmd);
                mcs.AddCommand(menuCmd);

                cmd = new CommandID(GuidList.guidSccProviderCmdSet, CommandId.icmdHgUpdateToRevision);
                menuCmd = new MenuCommand(new EventHandler(Exec_icmdHgUpdateToRevision), cmd);
                mcs.AddCommand(menuCmd);
                
            }

            // Register the provider with the source control manager
            // If the package is to become active, this will also callback on OnActiveStateChange and the menu commands will be enabled
            IVsRegisterScciProvider rscp = (IVsRegisterScciProvider)GetService(typeof(IVsRegisterScciProvider));
            rscp.RegisterSourceControlProvider(GuidList.guidSccProvider);

            _OnIdleEvent.RegisterForIdleTimeCallbacks(GetGlobalService(typeof(SOleComponentManager)) as IOleComponentManager);
            _OnIdleEvent.OnIdleEvent += new OnIdleEvent(sccService.UpdateDirtyNodesGlyphs);
        }

        protected override void Dispose(bool disposing)
        {
            Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture, "Entering Dispose() of: {0}", this.ToString()));

            _OnIdleEvent.OnIdleEvent -= new OnIdleEvent(sccService.UpdateDirtyNodesGlyphs); 
            _OnIdleEvent.UnRegisterForIdleTimeCallbacks();
            
            sccService.Dispose();

            base.Dispose(disposing);
        }

        // Returns the name of the source control provider
        public string ProviderName
        {
            get { return _strProviderName; }
        }

#endregion

        #region Source Control Command Enabling IOleCommandTarget.QueryStatus

        /// <summary>
        /// The shell call this function to know if a menu item should be visible and
        /// if it should be enabled/disabled.
        /// Note that this function will only be called when an instance of this editor
        /// is open.
        /// </summary>
        /// <param name="guidCmdGroup">Guid describing which set of command the current command(s) belong to</param>
        /// <param name="cCmds">Number of command which status are being asked for</param>
        /// <param name="prgCmds">Information for each command</param>
        /// <param name="pCmdText">Used to dynamically change the command text</param>
        /// <returns>HRESULT</returns>
        public int QueryStatus(ref Guid guidCmdGroup, uint cCmds, OLECMD[] prgCmds, System.IntPtr pCmdText)
        {
            Debug.Assert(cCmds == 1, "Multiple commands");
            Debug.Assert(prgCmds != null, "NULL argument");

            if ((prgCmds == null))
                return VSConstants.E_INVALIDARG;

            // Filter out commands that are not defined by this package
            if (guidCmdGroup != GuidList.guidSccProviderCmdSet)
            {
                return (int)(Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED); ;
            }

            OLECMDF cmdf = OLECMDF.OLECMDF_SUPPORTED;

            // All source control commands needs to be hidden and disabled when the provider is not active
            if (!sccService.Active)
            {
                cmdf = cmdf | OLECMDF.OLECMDF_INVISIBLE;
                cmdf = cmdf & ~(OLECMDF.OLECMDF_ENABLED);

                prgCmds[0].cmdf = (uint)cmdf;
                return VSConstants.S_OK;
            }

            // Process our Commands
            switch (prgCmds[0].cmdID)
            {
                case CommandId.icmdHgStatus: // status
                    cmdf |= QueryStatus_icmdHgStatus();
                    cmdf |= OLECMDF.OLECMDF_ENABLED; 
                    break;

                case CommandId.icmdHgCommit: // commit
                    cmdf |= QueryStatus_icmdHgCommit();
                    cmdf |= OLECMDF.OLECMDF_ENABLED; 
                    break;

                case CommandId.icmdHgHistory: // history
                    cmdf |= QueryStatus_icmdHgHistory();
                    cmdf |= OLECMDF.OLECMDF_ENABLED; 
                    break;

                case CommandId.icmdHgSynchronize:
                    cmdf |= QueryStatus_icmdHgSynchronize();
                    cmdf |= OLECMDF.OLECMDF_ENABLED; 
                    break;

                case CommandId.icmdHgUpdateToRevision:
                    cmdf |= QueryStatus_icmdHgUpdateToRevision();
                    cmdf |= OLECMDF.OLECMDF_ENABLED;
                    break;

                case CommandId.icmdViewToolWindow:
                case CommandId.icmdToolWindowToolbarCommand:
                    // These commmands are always enabled when the provider is active
                    cmdf |= OLECMDF.OLECMDF_ENABLED;
                    break;

                default:
                    return (int)(Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED);
            }

            prgCmds[0].cmdf = (uint)cmdf;

            return VSConstants.S_OK;
        }

        OLECMDF QueryStatus_icmdHgCommit()
        {
            return OLECMDF.OLECMDF_SUPPORTED;
        }
   
        OLECMDF QueryStatus_icmdHgHistory()
        {
            return OLECMDF.OLECMDF_SUPPORTED;
        }

        OLECMDF QueryStatus_icmdHgStatus()
        {
            return OLECMDF.OLECMDF_SUPPORTED;
        }

        OLECMDF QueryStatus_icmdHgSynchronize()
        {
            return OLECMDF.OLECMDF_ENABLED;
        }

        OLECMDF QueryStatus_icmdHgUpdateToRevision()
        {
            return OLECMDF.OLECMDF_ENABLED;
        }


        #endregion

        #region Source Control Commands Execution

        private void Exec_icmdHgCommit(object sender, EventArgs e)
        {
            HGLib.HGTK.CommitDialog(GetSolutionFileName());
        }

        private void Exec_icmdHgHistory(object sender, EventArgs e)
        {
            HGLib.HGTK.LogDialog(GetSolutionFileName());
        }

        private void Exec_icmdHgStatus(object sender, EventArgs e)
        {
            HGLib.HGTK.StatusDialog(GetSolutionFileName());
        }

        private void Exec_icmdHgSynchronize(object sender, EventArgs e)
        {
            HGLib.HGTK.SyncDialog(GetSolutionFileName());
        }

        private void Exec_icmdHgUpdateToRevision(object sender, EventArgs e)
        {
            HGLib.HGTK.UpdateDialog(GetSolutionFileName());
        }
        

        // The function can be used to bring back the provider's toolwindow if it was previously closed
        private void Exec_icmdViewToolWindow(object sender, EventArgs e)
        {
            MsVsShell.ToolWindowPane window = this.FindToolWindow(typeof(HGPendingChangesToolWindow), 0, true);
            IVsWindowFrame windowFrame = null;
            if (window != null && window.Frame != null)
            {
                windowFrame = (IVsWindowFrame)window.Frame;
            }
            if (windowFrame != null)
            {
                ErrorHandler.ThrowOnFailure(windowFrame.Show());
            }
        }
        
        private void Exec_icmdToolWindowToolbarCommand(object sender, EventArgs e)
        {
            HGPendingChangesToolWindow window = (HGPendingChangesToolWindow)this.FindToolWindow(typeof(HGPendingChangesToolWindow), 0, true);

            if (window != null)
            {
                window.ToolWindowToolbarCommand();
            }
        }

        #endregion

        #region Source Control Utility Functions

        /// <summary>
        /// Returns a list of controllable projects in the solution
        /// </summary>
        public List<IVsSccProject2> GetLoadedControllableProjects()
        {
            var list = new List<IVsSccProject2>();
            // Hashtable mapHierarchies = new Hashtable();

            IVsSolution sol = (IVsSolution)this.GetService(typeof(SVsSolution));
            Guid rguidEnumOnlyThisType = new Guid();
            IEnumHierarchies ppenum = null;
            ErrorHandler.ThrowOnFailure(sol.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref rguidEnumOnlyThisType, out ppenum));

            IVsHierarchy[] rgelt = new IVsHierarchy[1];
            uint pceltFetched = 0;
            while (ppenum.Next(1, rgelt, out pceltFetched) == VSConstants.S_OK &&
                   pceltFetched == 1)
            {
                IVsSccProject2 sccProject2 = rgelt[0] as IVsSccProject2;
                if (sccProject2 != null)
                {
                    list.Add(sccProject2);
                }
            }

            return list;
        }

        public Hashtable GetLoadedControllableProjectsEnum()
        {
            Hashtable mapHierarchies = new Hashtable();

            IVsSolution sol = (IVsSolution)GetService(typeof(SVsSolution));
            Guid rguidEnumOnlyThisType = new Guid();
            IEnumHierarchies ppenum = null;
            ErrorHandler.ThrowOnFailure(sol.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref rguidEnumOnlyThisType, out ppenum));

            IVsHierarchy[] rgelt = new IVsHierarchy[1];
            uint pceltFetched = 0;
            while (ppenum.Next(1, rgelt, out pceltFetched) == VSConstants.S_OK && 
                   pceltFetched == 1)
            {
                IVsSccProject2 sccProject2 = rgelt[0] as IVsSccProject2;
                if (sccProject2 != null)
                {
                    mapHierarchies[rgelt[0]] =  true;
                }
            }

            return mapHierarchies;
        }

        /// <summary>
        /// Checks whether a solution exist
        /// </summary>
        /// <returns>True if a solution was created.</returns>
        bool IsThereASolution()
        {
            return (GetSolutionFileName() != null);
        }

        /// <summary>
        /// Gets the list of selected controllable project hierarchies
        /// </summary>
        /// <returns>True if a solution was created.</returns>
        private Hashtable GetSelectedHierarchies(ref IList<VSITEMSELECTION> sel, out bool solutionSelected)
        {
            // Initialize output arguments
            solutionSelected = false;

            Hashtable mapHierarchies = new Hashtable();
            foreach(VSITEMSELECTION vsItemSel in sel)
            {
                if (vsItemSel.pHier == null ||
                    (vsItemSel.pHier as IVsSolution) != null)
                {
                    solutionSelected = true;
                }

                // See if the selected hierarchy implements the IVsSccProject2 interface
                // Exclude from selection projects like FTP web projects that don't support SCC
                IVsSccProject2 sccProject2 = vsItemSel.pHier as IVsSccProject2;
                if (sccProject2 != null)
                {
                    mapHierarchies[vsItemSel.pHier] =  true;
                }
            }

            return mapHierarchies;
        }

        /// <summary>
        /// Gets the list of directly selected VSITEMSELECTION objects
		/// </summary>
		/// <returns>A list of VSITEMSELECTION objects</returns>
		private IList<VSITEMSELECTION> GetSelectedNodes()
		{
			// Retrieve shell interface in order to get current selection
			IVsMonitorSelection monitorSelection = this.GetService(typeof(IVsMonitorSelection)) as IVsMonitorSelection;
			Debug.Assert(monitorSelection != null, "Could not get the IVsMonitorSelection object from the services exposed by this project");
			if (monitorSelection == null)
			{
				throw new InvalidOperationException();
			}
            
			List<VSITEMSELECTION> selectedNodes = new List<VSITEMSELECTION>();
			IntPtr hierarchyPtr = IntPtr.Zero;
			IntPtr selectionContainer = IntPtr.Zero;
			try
			{
				// Get the current project hierarchy, project item, and selection container for the current selection
				// If the selection spans multiple hierachies, hierarchyPtr is Zero
				uint itemid;
				IVsMultiItemSelect multiItemSelect = null;
				ErrorHandler.ThrowOnFailure(monitorSelection.GetCurrentSelection(out hierarchyPtr, out itemid, out multiItemSelect, out selectionContainer));

                if (itemid != VSConstants.VSITEMID_SELECTION)
                {
				    // We only care if there are nodes selected in the tree
                    if (itemid != VSConstants.VSITEMID_NIL)
                    {
                        if (hierarchyPtr == IntPtr.Zero)
                        {
                            // Solution is selected
                            VSITEMSELECTION vsItemSelection;
                            vsItemSelection.pHier = null;
                            vsItemSelection.itemid = itemid;
                            selectedNodes.Add(vsItemSelection);
                        }
                        else
                        {
                            IVsHierarchy hierarchy = (IVsHierarchy)Marshal.GetObjectForIUnknown(hierarchyPtr);
                            // Single item selection
                            VSITEMSELECTION vsItemSelection;
                            vsItemSelection.pHier = hierarchy;
                            vsItemSelection.itemid = itemid;
                            selectedNodes.Add(vsItemSelection);
                        }
                    }
                }
                else
                {
                    if (multiItemSelect != null)
                    {
                        // This is a multiple item selection.

                        //Get number of items selected and also determine if the items are located in more than one hierarchy
                        uint numberOfSelectedItems;
                        int isSingleHierarchyInt;
                        ErrorHandler.ThrowOnFailure(multiItemSelect.GetSelectionInfo(out numberOfSelectedItems, out isSingleHierarchyInt));
                        bool isSingleHierarchy = (isSingleHierarchyInt != 0);

                        // Now loop all selected items and add them to the list 
                        Debug.Assert(numberOfSelectedItems > 0, "Bad number of selected itemd");
                        if (numberOfSelectedItems > 0)
                        {
                            VSITEMSELECTION[] vsItemSelections = new VSITEMSELECTION[numberOfSelectedItems];
                            ErrorHandler.ThrowOnFailure(multiItemSelect.GetSelectedItems(0, numberOfSelectedItems, vsItemSelections));
                            foreach (VSITEMSELECTION vsItemSelection in vsItemSelections)
                            {
                                selectedNodes.Add(vsItemSelection);
                            }
                        }
                    }
                }
			}
			finally
			{
				if (hierarchyPtr != IntPtr.Zero)
				{
					Marshal.Release(hierarchyPtr);
				}
				if (selectionContainer != IntPtr.Zero)
				{
					Marshal.Release(selectionContainer);
				}
			}

			return selectedNodes;
		}

        /// <summary>
        /// Returns a list of source controllable files in the selection (recursive)
        /// </summary>
        private IList<string> GetSelectedFilesInControlledProjects()
        {
            IList<VSITEMSELECTION> selectedNodes = null;
            return GetSelectedFilesInControlledProjects(out selectedNodes);
        }

        /// <summary>
        /// Returns a list of source controllable files in the selection (recursive)
        /// </summary>
        private IList<string> GetSelectedFilesInControlledProjects(out IList<VSITEMSELECTION> selectedNodes)
        {
            IList<string> sccFiles = new List<string>();

            selectedNodes = GetSelectedNodes();
            bool isSolutionSelected = false;
            Hashtable hash = GetSelectedHierarchies(ref selectedNodes, out isSolutionSelected);
            if (isSolutionSelected)
            {
                // Replace the selection with the root items of all controlled projects
                selectedNodes.Clear();
                Hashtable hashControllable = GetLoadedControllableProjectsEnum();
                foreach (IVsHierarchy pHier in hashControllable.Keys)
                {
                    if (sccService.IsProjectControlled(pHier))
                    {
                        VSITEMSELECTION vsItemSelection;
                        vsItemSelection.pHier = pHier;
                        vsItemSelection.itemid = VSConstants.VSITEMID_ROOT;
                        selectedNodes.Add(vsItemSelection);
                    }
                }

                // Add the solution file to the list
                if (sccService.IsProjectControlled(null))
                {
                    IVsHierarchy solHier = (IVsHierarchy)GetService(typeof(SVsSolution));
                    VSITEMSELECTION vsItemSelection;
                    vsItemSelection.pHier = solHier;
                    vsItemSelection.itemid = VSConstants.VSITEMID_ROOT;
                    selectedNodes.Add(vsItemSelection);
                }
            }

            // now look in the rest of selection and accumulate scc files
            foreach (VSITEMSELECTION vsItemSel in selectedNodes)
            {
                IVsSccProject2 pscp2 = vsItemSel.pHier as IVsSccProject2;
                if (pscp2 == null)
                {
                    // solution case
                    sccFiles.Add(GetSolutionFileName());
                }
                else
                {
                    IList<string> nodefilesrec = GetProjectFiles(pscp2, vsItemSel.itemid);
                    foreach (string file in nodefilesrec)
                    {
                        sccFiles.Add(file);
                    }
                }
            }

            return sccFiles;
        }

        /// <summary>
        /// Returns a list of source controllable files associated with the specified node
        /// </summary>
        public static IList<string> GetNodeFiles(IVsHierarchy hier, uint itemid)
        {
            IVsSccProject2 pscp2 = hier as IVsSccProject2;
            return GetNodeFiles(pscp2, itemid);
        }

        /// <summary>
        /// Returns a list of source controllable files associated with the specified node
        /// </summary>
        private static IList<string> GetNodeFiles(IVsSccProject2 pscp2, uint itemid)
        {
            // NOTE: the function returns only a list of files, containing both regular files and special files
            // If you want to hide the special files (similar with solution explorer), you may need to return 
            // the special files in a hastable (key=master_file, values=special_file_list)

            // Initialize output parameters
            IList<string> sccFiles = new List<string>();
            if (pscp2 != null)
            {
                CALPOLESTR[] pathStr = new CALPOLESTR[1];
                CADWORD[] flags = new CADWORD[1];

                if (pscp2.GetSccFiles(itemid, pathStr, flags) == 0)
                {
                    for (int elemIndex = 0; elemIndex < pathStr[0].cElems; elemIndex++)
                    {
                        IntPtr pathIntPtr = Marshal.ReadIntPtr(pathStr[0].pElems, elemIndex);
                        String path = Marshal.PtrToStringAuto(pathIntPtr);

                        sccFiles.Add(path);

                        // See if there are special files
                        if (flags.Length > 0 && flags[0].cElems > 0)
                        {
                            int flag = Marshal.ReadInt32(flags[0].pElems, elemIndex);

                            if (flag != 0)
                            {
                                // We have special files
                                CALPOLESTR[] specialFiles = new CALPOLESTR[1];
                                CADWORD[] specialFlags = new CADWORD[1];

                                pscp2.GetSccSpecialFiles(itemid, path, specialFiles, specialFlags);
                                for (int i = 0; i < specialFiles[0].cElems; i++)
                                {
                                    IntPtr specialPathIntPtr = Marshal.ReadIntPtr(specialFiles[0].pElems, i * IntPtr.Size);
                                    String specialPath = Marshal.PtrToStringAuto(specialPathIntPtr);

                                    sccFiles.Add(specialPath);
                                    Marshal.FreeCoTaskMem(specialPathIntPtr);
                                }

                                if (specialFiles[0].cElems > 0)
                                {
                                    Marshal.FreeCoTaskMem(specialFiles[0].pElems);
                                }
                            }
                        }

                        Marshal.FreeCoTaskMem(pathIntPtr);
                    }
                    if (pathStr[0].cElems > 0)
                    {
                        Marshal.FreeCoTaskMem(pathStr[0].pElems);
                    }
                }
            }

            return sccFiles;
        }

        /// <summary>
        /// Refreshes the glyphs of the specified hierarchy nodes
        /// </summary>
        public void RefreshNodesGlyphs(IList<VSITEMSELECTION> selectedNodes)
        {
            foreach (VSITEMSELECTION vsItemSel in selectedNodes)
            {
                IVsSccProject2 sccProject2 = vsItemSel.pHier as IVsSccProject2;
                if (vsItemSel.itemid == VSConstants.VSITEMID_ROOT)
                {
                    if (sccProject2 == null)
                    {
                        // Note: The solution's hierarchy does not implement IVsSccProject2, IVsSccProject interfaces
                        // It may be a pain to treat the solution as special case everywhere; a possible workaround is 
                        // to implement a solution-wrapper class, that will implement IVsSccProject2, IVsSccProject and
                        // IVsHierarhcy interfaces, and that could be used in provider's code wherever a solution is needed.
                        // This approach could unify the treatment of solution and projects in the provider's code.

                        // Until then, solution is treated as special case
                        string[] rgpszFullPaths = new string[1];
                        rgpszFullPaths[0] = GetSolutionFileName();
                        VsStateIcon[] rgsiGlyphs = new VsStateIcon[1];
                        uint[] rgdwSccStatus = new uint[1];
                        sccService.GetSccGlyph(1, rgpszFullPaths, rgsiGlyphs, rgdwSccStatus);

                        // Set the solution's glyph directly in the hierarchy
                        IVsHierarchy solHier = (IVsHierarchy)GetService(typeof(SVsSolution));
                        solHier.SetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_StateIconIndex, rgsiGlyphs[0]);
                    }
                    else
                    {
                        // Refresh all the glyphs in the project; the project will call back GetSccGlyphs() 
                        // with the files for each node that will need new glyph
                        sccProject2.SccGlyphChanged(0, null, null, null);
                    }
                }
                else
                {
                    // It may be easier/faster to simply refresh all the nodes in the project, 
                    // and let the project call back on GetSccGlyphs, but just for the sake of the demo, 
                    // let's refresh ourselves only one node at a time
                    IList<string> sccFiles = GetNodeFiles(sccProject2, vsItemSel.itemid);
                    
                    // We'll use for the node glyph just the Master file's status (ignoring special files of the node)
                    if (sccFiles.Count > 0)
                    {
                        string[] rgpszFullPaths = new string[1];
                        rgpszFullPaths[0] = sccFiles[0];
                        VsStateIcon[] rgsiGlyphs = new VsStateIcon[1];
                        uint[] rgdwSccStatus = new uint[1];
                        sccService.GetSccGlyph(1, rgpszFullPaths, rgsiGlyphs, rgdwSccStatus);

                        uint[] rguiAffectedNodes = new uint[1];
                        rguiAffectedNodes[0] = vsItemSel.itemid;
                        sccProject2.SccGlyphChanged(1, rguiAffectedNodes, rgsiGlyphs, rgdwSccStatus);
                    }
                }
            }
        }


        /// <summary>
        /// Returns the filename of the solution
        /// </summary>
        public string GetSolutionFileName()
        {
            IVsSolution sol = (IVsSolution)GetService(typeof(SVsSolution));
            string solutionDirectory, solutionFile, solutionUserOptions;
            if (sol.GetSolutionInfo(out solutionDirectory, out solutionFile, out solutionUserOptions) == VSConstants.S_OK)
            {
                return solutionFile;
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the filename of the specified controllable project 
        /// </summary>
        public static string GetProjectFileName(VisualHG.SccProvider provider, IVsSccProject2 pscp2Project)
        {
            // Note: Solution folders return currently a name like "NewFolder1{1DBFFC2F-6E27-465A-A16A-1AECEA0B2F7E}.storage"
            // Your provider may consider returning the solution file as the project name for the solution, if it has to persist some properties in the "project file"
            // UNDONE: What to return for web projects? They return a folder name, not a filename! Consider returning a pseudo-project filename instead of folder.

            IVsHierarchy hierProject = (IVsHierarchy)pscp2Project;
            IVsProject project = (IVsProject)pscp2Project;

            // Attempt to get first the filename controlled by the root node 
            IList<string> sccFiles = GetNodeFiles(pscp2Project, VSConstants.VSITEMID_ROOT);
            if (sccFiles.Count > 0 && sccFiles[0] != null && sccFiles[0].Length > 0)
            {
                return sccFiles[0];
            }

            // If that failed, attempt to get a name from the IVsProject interface
            string bstrMKDocument;
            if (project.GetMkDocument(VSConstants.VSITEMID_ROOT, out bstrMKDocument) == VSConstants.S_OK &&
                bstrMKDocument != null && bstrMKDocument.Length > 0)
            {
                return bstrMKDocument;
            }

            // If that failes, attempt to get the filename from the solution
            IVsSolution sol = (IVsSolution)provider.GetService(typeof(SVsSolution));
            string uniqueName;
            if (sol.GetUniqueNameOfProject(hierProject, out uniqueName) == VSConstants.S_OK &&
                uniqueName != null && uniqueName.Length > 0)
            {
                // uniqueName may be a full-path or may be relative to the solution's folder
                if (uniqueName.Length > 2 && uniqueName[1] == ':')
                {
                    return uniqueName;
                }

                // try to get the solution's folder and relativize the project name to it
                string solutionDirectory, solutionFile, solutionUserOptions;
                if (sol.GetSolutionInfo(out solutionDirectory, out solutionFile, out solutionUserOptions) == VSConstants.S_OK)
                {
                    uniqueName = solutionDirectory + "\\" + uniqueName;
                    
                    // UNDONE: eliminate possible "..\\.." from path
                    return uniqueName;
                }
            }

            // If that failed, attempt to get the project name from 
            string bstrName;
            if (hierProject.GetCanonicalName(VSConstants.VSITEMID_ROOT, out bstrName) == VSConstants.S_OK)
            {
                return bstrName;
            }

            // if everything we tried fail, return null string
            return null;
        }

        private static void DebugWalkingNode(IVsHierarchy pHier, uint itemid)
        {
            object property = null;
            if (pHier.GetProperty(itemid, (int)__VSHPROPID.VSHPROPID_Name, out property) == VSConstants.S_OK)
            {
                Trace.WriteLine(String.Format(CultureInfo.CurrentUICulture, "Walking hierarchy node: {0}", (string)property));
            }
        }

        /// <summary>
        /// Gets the list of ItemIDs that are nodes in the specified project
		/// </summary>
        private static IList<uint> GetProjectItems(IVsHierarchy pHier)
        {
            // Start with the project root and walk all expandable nodes in the project
            return GetProjectItems(pHier, VSConstants.VSITEMID_ROOT);
        }

        /// <summary>
        /// Gets the list of ItemIDs that are nodes in the specified project, starting with the specified item
		/// </summary>
        private static IList<uint> GetProjectItems(IVsHierarchy pHier, uint startItemid)
        {
            List<uint> projectNodes = new List<uint>();

            // The method does a breadth-first traversal of the project's hierarchy tree
            Queue<uint> nodesToWalk = new Queue<uint>();
            nodesToWalk.Enqueue(startItemid);

            while (nodesToWalk.Count > 0)
            {
                uint node = nodesToWalk.Dequeue();
                projectNodes.Add(node);

                DebugWalkingNode(pHier, node);

                object property = null;
                if (pHier.GetProperty(node, (int)__VSHPROPID.VSHPROPID_FirstChild, out property) == VSConstants.S_OK)
                {
                    uint childnode = (uint)(int)property;
                    if (childnode == VSConstants.VSITEMID_NIL)
                    {
                        continue;
                    }

                    DebugWalkingNode(pHier, childnode);

                    if ((pHier.GetProperty(childnode, (int)__VSHPROPID.VSHPROPID_Expandable, out property) == VSConstants.S_OK && (int)property != 0) ||
                        (pHier.GetProperty(childnode, (int)__VSHPROPID2.VSHPROPID_Container, out property) == VSConstants.S_OK && (bool)property))
                    {
                        nodesToWalk.Enqueue(childnode);
                    }
                    else
                    {
                        projectNodes.Add(childnode);
                    }

                    while (pHier.GetProperty(childnode, (int)__VSHPROPID.VSHPROPID_NextSibling, out property) == VSConstants.S_OK)
                    {
                        childnode = (uint)(int)property;
                        if (childnode == VSConstants.VSITEMID_NIL)
                        {
                            break;
                        }

                        DebugWalkingNode(pHier, childnode);

                        if ((pHier.GetProperty(childnode, (int)__VSHPROPID.VSHPROPID_Expandable, out property) == VSConstants.S_OK && (int)property != 0) ||
                            (pHier.GetProperty(childnode, (int)__VSHPROPID2.VSHPROPID_Container, out property) == VSConstants.S_OK && (bool)property)  ) 
                        {
                            nodesToWalk.Enqueue(childnode);
                        }
                        else
                        {
                            projectNodes.Add(childnode);
                        }
                    }
                }

            }

            return projectNodes;
        }

        /// <summary>
        /// Gets the list of source controllable files in the specified project
        /// </summary>
        public static IList<string> GetProjectFiles(IVsSccProject2 pscp2Project)
        {
            return GetProjectFiles(pscp2Project, VSConstants.VSITEMID_ROOT);
        }

        /// <summary>
        /// Gets the list of source controllable files in the specified project
        /// </summary>
        public static IList<string> GetProjectFiles(IVsSccProject2 pscp2Project, uint startItemId)
        {
            IList<string> projectFiles = new List<string>();
            IVsHierarchy hierProject = (IVsHierarchy)pscp2Project;
            IList<uint> projectItems = GetProjectItems(hierProject, startItemId);

            foreach (uint itemid in projectItems)
            {
                IList<string> sccFiles = GetNodeFiles(pscp2Project, itemid);
                foreach (string file in sccFiles)
                {
                    projectFiles.Add(file);
                }
            }

            return projectFiles;
        }

        /// <summary>
        /// Checks whether the provider is invoked in command line mode
        /// </summary>
        public bool InCommandLineMode()
        {
            IVsShell shell = (IVsShell)GetService(typeof(SVsShell));
            object pvar;
            if (shell.GetProperty((int)__VSSPROPID.VSSPROPID_IsInCommandLineMode, out pvar) == VSConstants.S_OK &&
                (bool)pvar)
            {
                return true;
            }

            return false;
        }
   
        #endregion
    }
}