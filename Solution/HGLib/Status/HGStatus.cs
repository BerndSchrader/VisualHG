﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Windows.Forms;
using System.Threading;
using System.Timers;

namespace HGLib
{
    // ---------------------------------------------------------------------------
    // source control file status enum
    // ---------------------------------------------------------------------------
    public enum HGFileStatus
    {
        scsUncontrolled = 0x001,
        scsClean        = 0x002,
        scsModified     = 0x004,
        scsAdded        = 0x008,
        scsRemoved      = 0x010,
        scsRenamed      = 0x020,
        scsCopied       = 0x040,
        scsIgnored      = 0x080,
        scsMissing      = 0x100,
    };

    // ---------------------------------------------------------------------------
    // A delegate type for getting statu changed notifications
    // ---------------------------------------------------------------------------
    public delegate void HGStatusChangedEvent();
    
    // ---------------------------------------------------------------------------
    // HG file status cache. The states of the included files are stored in a
    // file status dictionary. Change events are availabe by HGStatusChanged delegate.
    // ---------------------------------------------------------------------------
    public class HGStatus
    {
        // ------------------------------------------------------------------------
        // status changed event 
        // ------------------------------------------------------------------------
        public event HGStatusChangedEvent HGStatusChanged;

        // file status cache
        HGFileStatusInfoDictionary _fileStatusDictionary = new HGFileStatusInfoDictionary();
        
        // directory watcher map - one for each main directory
        DirectoryWatcherMap _directoryWatcherMap = new DirectoryWatcherMap();

        // queued user commands or events from the IDE
        WorkItemQueue _workItemQueue = new WorkItemQueue();
        
        // root info class
        class RootInfo
        {
          // current branch name of root
          public string _Branch;
        };
        
        // HG repo root directories - also SubRepo dirs
        Dictionary<string, RootInfo> _rootDirMap = new Dictionary<string, RootInfo>();
        
        // trigger thread to observe and assimilate the directory watcher changed file dictionaries
        System.Timers.Timer _timerDirectoryStatusChecker;

        // build process is active
        volatile bool       _IsSolutionBuilding = false;
        
        // synchronize to WindowsForms context
        SynchronizationContext _context;

        // Flag to avoid to much rebuild action when .HG\dirstate was changed.
        // Extenal changes of the dirstate file results definitely in cache rebuild.
        // Changes caused by ourself should not.
        bool         _SkipDirstate = false;

        // min elapsed time before cache rebild trigger.
        volatile int _MinElapsedTimeForStatusCacheRebuildMS = 2000;

        // complete cache rebuild is required
        volatile bool _bRebuildStatusCacheRequired = false;

        // ------------------------------------------------------------------------
        // init objects and starts the directory watcher
        // ------------------------------------------------------------------------
        public HGStatus()
        {
            StartDirectoryStatusChecker();
        }

        // ------------------------------------------------------------------------
        // skip / enable dirstate file changes
        // ------------------------------------------------------------------------
        public void SkipDirstate(bool skip)
        {
            _SkipDirstate = skip;
        }

        // ------------------------------------------------------------------------
        // set reset rebuild status flag
        // ------------------------------------------------------------------------
        public bool RebuildStatusCacheRequiredFlag { set { _bRebuildStatusCacheRequired = value; } }

        // ------------------------------------------------------------------------
        // toggle directory watching on / off
        // ------------------------------------------------------------------------
        public void EnableDirectoryWatching(bool enable)
        {
            _directoryWatcherMap.EnableDirectoryWatching(enable);
        }

        // ------------------------------------------------------------------------
        // add one work item to work queue
        // ------------------------------------------------------------------------
        public void AddWorkItem(IHGWorkItem workItem)
        {
            lock (_workItemQueue)
            {
                _workItemQueue.Enqueue(workItem);
            }    
        }

        // ------------------------------------------------------------------------
        // GetFileStatus info for the given filename
        // ------------------------------------------------------------------------
        public bool GetFileStatusInfo(string fileName, out HGFileStatusInfo info)
        {
            return _fileStatusDictionary.TryGetValue(fileName, out info);
        }
        
        // ------------------------------------------------------------------------
        // Create pending files list
        // ------------------------------------------------------------------------
        public void CreatePendingFilesList(out List<HGFileStatusInfo> list )
        {
            lock (_fileStatusDictionary)
            {
              _fileStatusDictionary.CreatePendingFilesList(out list);
            }
        }
    
        // ------------------------------------------------------------------------
        // GetFileStatus for the given filename
        // ------------------------------------------------------------------------
        public HGFileStatus GetFileStatus(string fileName)
        {
            if (_context == null)
                _context = WindowsFormsSynchronizationContext.Current;

            bool found = false;
            HGFileStatusInfo value;

            lock (_fileStatusDictionary)
            {
                found = _fileStatusDictionary.TryGetValue(fileName, out value);
            }

            return (found ? value.status : HGFileStatus.scsUncontrolled);
        }

        // ------------------------------------------------------------------------
        // fire status changed event
        // ------------------------------------------------------------------------
        void FireStatusChanged(SynchronizationContext context)
        {
            if (HGStatusChanged != null)
            {
                if (context != null)
                {
                    context.Post(new SendOrPostCallback( x =>
                    {
                        HGStatusChanged();
                    }), null);
                }
                else
                {
                    HGStatusChanged();
                }
            }
        }

        public bool AnyItemsUnderSourceControl()
        {
            return (_directoryWatcherMap.Count > 0);
        }

        // ------------------------------------------------------------------------
        // SetCacheDirty triggers a RebuildStatusCache event
        // ------------------------------------------------------------------------
        public void SetCacheDirty()
        {
            _bRebuildStatusCacheRequired = true;
        }

        // ------------------------------------------------------------------------
        /// update given file status.
        // ------------------------------------------------------------------------
        public void UpdateFileStatus(string[] files)
        {
            Dictionary<string, char> fileStatusDictionary;
            if (HG.QueryFileStatus(files, out fileStatusDictionary)) 
            {
                _fileStatusDictionary.Add(fileStatusDictionary);
            }
        }

        // ------------------------------------------------------------------------
        // update given root files status.
        // ------------------------------------------------------------------------
        public void UpdateFileStatus(string root)
        {
            Dictionary<string, char> fileStatusDictionary;
            if (HG.QueryRootStatus(root, out fileStatusDictionary))
            {
                _fileStatusDictionary.Add(fileStatusDictionary);
            }
        }

        // ------------------------------------------------------------------------
        /// Add a root directory and query the status of the contining files 
        /// by a QueryRootStatus call.
        // ------------------------------------------------------------------------
        public bool AddRootDirectory(string directory)
        {
            if (directory == string.Empty)
                return false;

            string root = HG.FindRootDirectory(directory);
            if (root != string.Empty && !_rootDirMap.ContainsKey(root))
            {
                _rootDirMap[root] = new RootInfo() { _Branch = HG.GetCurrentBranchName(root) };

                if (!_directoryWatcherMap.ContainsDirectory(root))
                {
                    _directoryWatcherMap.WatchDirectory(root);
                }

                Dictionary<string, char> fileStatusDictionary;
                if (HG.QueryRootStatus(root, out fileStatusDictionary))
                {
                    _fileStatusDictionary.Add(fileStatusDictionary);
                }
            }

            return true;
        }

        // ------------------------------------------------------------------------
        // get current used brunch of the given roor directory
        // ------------------------------------------------------------------------
        public string GetCurrentBranchOf(string root)
        {
          RootInfo info;
          
          if( _rootDirMap.TryGetValue(root, out info) )
            return info._Branch;
          
          return string.Empty;  
          
        }

        // ------------------------------------------------------------------------
        // format current brunch
        // ------------------------------------------------------------------------
        public string FormatBranchList()
        {
            string branchList = string.Empty;
            
            lock (_rootDirMap)
            {
                SortedList<string,int> branches = new SortedList<string,int>();

                //RootInfo info;
                foreach (RootInfo info in _rootDirMap.Values)
                {
                    if (!branches.ContainsKey(info._Branch))
                    {
                        branches.Add(info._Branch, 0);
                        branchList = branchList + (branches.Count > 1 ? ", " : "") + info._Branch;
                    }
                }
            }
            
            return branchList;
        }

        #region dirstatus changes

        // ------------------------------------------------------------------------
        /// add file to the repositiry if they are not on the ignore list
        // ------------------------------------------------------------------------
        public void AddNotIgnoredFiles(string[] fileListRaw)
        {
            // filter already known files from the list
            List<string> fileList = new List<string>();
            lock (_fileStatusDictionary)
            {
              foreach (string file in fileListRaw)
              {
                HGFileStatusInfo info;
                if(!_fileStatusDictionary.TryGetValue(file.ToLower(), out info) || info.status != HGFileStatus.scsIgnored)
                {
                    fileList.Add(file);
                }
              }
            }

            if (fileList.Count==0)
              return;
            
            
            SkipDirstate(true);
            Dictionary<string, char> fileStatusDictionary;
            if (HG.AddFilesNotIgnored(fileList.ToArray(), out fileStatusDictionary))
            {
                _fileStatusDictionary.Add(fileStatusDictionary);
            }
            SkipDirstate(false);
        }

        // ------------------------------------------------------------------------
        /// enter file renamed to hg repository
        // ------------------------------------------------------------------------
        public void EnterFileRenamed(string[] oldFileNames, string[] newFileNames)
        {
            var oNameList = new List<string> ();
            var nNameList = new List<string>();

            lock (_fileStatusDictionary)
            {
                for (int pos = 0; pos < oldFileNames.Length; ++pos)
                {
                    string oFileName = oldFileNames[pos];
                    string nFileName = newFileNames[pos];

                    if (nFileName.EndsWith("\\"))
                    {
                        // this is an dictionary - skip it
                    }
                    else if (oFileName.ToLower() != nFileName.ToLower())
                    {
                        oNameList.Add(oFileName);
                        nNameList.Add(nFileName);
                        _fileStatusDictionary.Remove(oFileName);
                        _fileStatusDictionary.Remove(nFileName);
                    }
                }
            }

            if (oNameList.Count>0)
            {
                SkipDirstate(true);
                HG.EnterFileRenamed(oNameList.ToArray(), nNameList.ToArray());
                SkipDirstate(false);
            }
        }

        // ------------------------------------------------------------------------
        // remove given file from cache
        // ------------------------------------------------------------------------
        public void RemoveFileFromCache(string file)
        {
            lock (_fileStatusDictionary)
            {
                _fileStatusDictionary.Remove(file);
            }
        }

        // ------------------------------------------------------------------------
        // file was removed - now update the hg repository
        // ------------------------------------------------------------------------
        public void EnterFilesRemoved(string[] fileList)
        {
            List<string> removedFileList = new List<string>();
            List<string> movedFileList   = new List<string>();
            List<string> newNamesList    = new List<string>();
            
            lock (_fileStatusDictionary)
            {
                foreach (var file in fileList)
                {
                    _fileStatusDictionary.Remove(file);
                    
                    string newName;
                    if (!_fileStatusDictionary.FileMoved(file, out newName))
                        removedFileList.Add(file);
                    else
                    {
                        movedFileList.Add(file);
                        newNamesList.Add(newName);
                    }
                }
            }

            if (movedFileList.Count > 0)
                EnterFileRenamed(movedFileList.ToArray(), newNamesList.ToArray());

            if (removedFileList.Count > 0)
            {
                SkipDirstate(true); // avoid a status requery for the repo after hg.dirstate was changed
                Dictionary<string, char> fileStatusDictionary;
                if (HG.EnterFileRemoved(removedFileList.ToArray(), out fileStatusDictionary))
                {
                    _fileStatusDictionary.Add(fileStatusDictionary);
                }
                SkipDirstate(false);
            }
        }

        #endregion dirstatus changes

        // ------------------------------------------------------------------------
        // clear the complete cache data
        // ------------------------------------------------------------------------
        public void ClearStatusCache()
        {
            lock (_directoryWatcherMap)
            {
                _directoryWatcherMap.UnsubscribeEvents();
                _directoryWatcherMap.Clear();
                
            }
            
            lock(_rootDirMap)
            {
                _rootDirMap.Clear();
            }

            lock (_fileStatusDictionary)
            {
                _fileStatusDictionary.Clear();
            }
        }

        // ------------------------------------------------------------------------
        // rebuild the entire _fileStatusDictionary map
        // this includes all files in all watched directories
        // ------------------------------------------------------------------------
        void RebuildStatusCache()
        {
            // remove all status entries
            _fileStatusDictionary.Clear();

            _bRebuildStatusCacheRequired = false;
            
            SkipDirstate(true);
            
            HGFileStatusInfoDictionary newFileStatusDictionary = new HGFileStatusInfoDictionary();
            foreach (var directoryWatcher in _directoryWatcherMap.WatcherList)
            {
                // reset the watcher map
                directoryWatcher.Value.PopDirtyFilesMap();
            }

            List<string> rootDirList = null; 
            lock (_rootDirMap)
            {
                rootDirList = new List<string>(_rootDirMap.Keys);
            }

            // sort dirs by lenght to query from root top to down root
            rootDirList.Sort((a, b) => ((a.Length == b.Length) ? 0 : ((a.Length > b.Length) ? 1 : -1)) );
            foreach (string rootDirectory in rootDirList)
            {
                if (rootDirectory != string.Empty)
                {
                    _rootDirMap[rootDirectory]._Branch = HG.GetCurrentBranchName(rootDirectory); 
                    
                    Dictionary<string, char> fileStatusDictionary;
                    if (HG.QueryRootStatus(rootDirectory, out fileStatusDictionary))
                    {
                        Trace.WriteLine("RebuildStatusCache - number of files: " + fileStatusDictionary.Count.ToString());
                        newFileStatusDictionary.Add(fileStatusDictionary);
                    }
                }
            }

            lock (_fileStatusDictionary)
            {
                _fileStatusDictionary = newFileStatusDictionary;
            }
            
            SkipDirstate(false);
        }

        // ------------------------------------------------------------------------
        // directory watching
        // ------------------------------------------------------------------------
        #region directory watcher

        // ------------------------------------------------------------------------
        // start the trigger thread
        // ------------------------------------------------------------------------
        void StartDirectoryStatusChecker()
        {
            _timerDirectoryStatusChecker = new System.Timers.Timer();
            _timerDirectoryStatusChecker.Elapsed += new ElapsedEventHandler(DirectoryStatusCheckerThread);
            _timerDirectoryStatusChecker.AutoReset = false;
            _timerDirectoryStatusChecker.Interval = 100;
            _timerDirectoryStatusChecker.Enabled  = true;
        }

        public void UpdateSolution_StartUpdate()
        {
            _IsSolutionBuilding = true;
        }

        public void UpdateSolution_Done()
        {
            _IsSolutionBuilding = false;
        }
            
        // ------------------------------------------------------------------------
        // async proc to assimilate the directory watcher state dictionaries
        // ------------------------------------------------------------------------
        void DirectoryStatusCheckerThread(object source, ElapsedEventArgs e)
        {
            // handle user and IDE commands first
            Queue<IHGWorkItem> workItemQueue = _workItemQueue.PopWorkItems();
            if (workItemQueue.Count > 0)
            {
                List<string> ditryFilesList = new List<string>();
                foreach (IHGWorkItem item in workItemQueue)
                {
                    item.Do(this, ditryFilesList);
                }

                if (ditryFilesList.Count>0)
                {
                    Dictionary<string, char> fileStatusDictionary;
                    if (HG.QueryFileStatus(ditryFilesList.ToArray(), out fileStatusDictionary))
                    {
                        lock (_fileStatusDictionary)
                        {
                            _fileStatusDictionary.Add(fileStatusDictionary);
                        }
                    }
                }
                
                // update status icons
                FireStatusChanged(_context);
            }
            else if (!_IsSolutionBuilding)
            {
                // handle modified files list
                long numberOfControlledFiles = 0;
                lock (_fileStatusDictionary)
                {
                    numberOfControlledFiles = System.Math.Max(1, _fileStatusDictionary.Count);
                }

                long numberOfChangedFiles = 0;
                double elapsedMS = 0;
                lock (_directoryWatcherMap)
                {
                    numberOfChangedFiles = _directoryWatcherMap.GetNumberOfChangedFiles();
                    TimeSpan timeSpan = new TimeSpan(DateTime.Now.Ticks - _directoryWatcherMap.GetLatestChange().Ticks);
                    elapsedMS = timeSpan.TotalMilliseconds;
                }

                if (_bRebuildStatusCacheRequired || numberOfChangedFiles > 200)
                {
                    if (elapsedMS > _MinElapsedTimeForStatusCacheRebuildMS)
                    {
                        Trace.WriteLine("DoFullStatusUpdate (NumberOfChangedFiles: " + numberOfChangedFiles.ToString() + " )");
                        RebuildStatusCache();
                        // update status icons
                        FireStatusChanged(_context);
                    }
                }
                else if (numberOfChangedFiles > 0)
                {
                    // min elapsed time before do anything
                    if (elapsedMS > 2000)
                    {
                        Trace.WriteLine("UpdateDirtyFilesStatus (NumberOfChangedFiles: " + numberOfChangedFiles.ToString() + " )");
                        var fileList = PopDirtyWatcherFiles();
                        if( UpdateFileStatusDictionary(fileList) )
                        {
                            // update status icons - but only if a project file was changed
                            bool bFireStatusChanged = false;
                            lock(_FileToProjectCache)
                            {
                                foreach (string file in fileList)
                                {
                                    object o;
                                    if (_FileToProjectCache.TryGetValue(file.ToLower(), out o))
                                    {
                                        bFireStatusChanged = true;
                                        break;
                                    }
                                }
                            }

                            if(bFireStatusChanged)
                                FireStatusChanged(_context);
                        }
                    }
                }
            }
        
            _timerDirectoryStatusChecker.Enabled = true;
        }

        // ------------------------------------------------------------------------
        // Check if the watched file is the hg/dirstate and set _bRebuildStatusCacheRequred to true if required
        // Check if the file state must be refreshed
        // Return: true if the file is dirty, false if not
        // ------------------------------------------------------------------------
        bool PrepareWatchedFile(string fileName)
        {
            bool isDirty = true;

            if (DirectoryWatcher.DirectoryExists(fileName))
            {
                // directories are not controlled
                isDirty = false;
            }
            else if (fileName.IndexOf(".hg\\dirstate") > -1)
            {
                if (!_SkipDirstate)
                {
                    _bRebuildStatusCacheRequired = true;
                    Trace.WriteLine("   ... rebuild of status cache required");
                }
                isDirty = false;
            }
            else if (fileName.IndexOf("\\.hg") != -1)
            {
                // all other .hg files are ignored
                isDirty = false;
            }
            else
            {
                HGFileStatusInfo hgFileStatusInfo;
                
                lock (_fileStatusDictionary)
                {
                    _fileStatusDictionary.TryGetValue(fileName, out hgFileStatusInfo);
                }

                if (hgFileStatusInfo != null)
                {
                    FileInfo fileInfo = new FileInfo(fileName);
                    if (fileInfo.Exists)
                    {
                        // see if the file states are equal
                        if ((hgFileStatusInfo.timeStamp == fileInfo.LastWriteTime &&
                             hgFileStatusInfo.size == fileInfo.Length))
                        {
                            isDirty = false;
                        }
                    }
                    else
                    {
                        if (hgFileStatusInfo.status == HGFileStatus.scsRemoved || hgFileStatusInfo.status == HGFileStatus.scsUncontrolled)
                        {
                            isDirty = false;
                        }
                    }
                }
            }
            return isDirty;
        }

        /// <summary>
        /// list all modified files of all watchers into the return list and reset 
        /// watcher files maps
        /// </summary>
        /// <returns></returns>
        private List<string> PopDirtyWatcherFiles()
        {
            var fileList = new List<string>(); 
            foreach (var directoryWatcher in _directoryWatcherMap.WatcherList)
            {
                var dirtyFilesMap = directoryWatcher.Value.PopDirtyFilesMap();
                if (dirtyFilesMap.Count > 0)
                {
                    // first collect dirty files list
                    foreach (var dirtyFile in dirtyFilesMap)
                    {
                        if (PrepareWatchedFile(dirtyFile.Key) && !_bRebuildStatusCacheRequired)
                        {
                            fileList.Add(dirtyFile.Key);
                        }

                        // could be set by PrepareWatchedFile
                        if (_bRebuildStatusCacheRequired)
                            break;
                    }
                }
            }
            return fileList;
        }

        // ------------------------------------------------------------------------
        // update file status of the watched dirty files
        // ------------------------------------------------------------------------
        bool UpdateFileStatusDictionary(List<string> fileList)
        {
            bool updateUI = false;

            if (_bRebuildStatusCacheRequired)
                return false;

            // now we will get HG status information for the remaining files
            if (!_bRebuildStatusCacheRequired && fileList.Count > 0)
            {
                Dictionary<string, char> fileStatusDictionary;
                SkipDirstate(true); 
                if (HG.QueryFileStatus(fileList.ToArray(), out fileStatusDictionary))
                {
                    Trace.WriteLine("got status for watched files - count: " + fileStatusDictionary.Count.ToString());
                    lock (_fileStatusDictionary)
                    {
                        _fileStatusDictionary.Add(fileStatusDictionary);
                    }
                }
                SkipDirstate(false); 
                updateUI = true;
            }
            return updateUI;
        }

        #endregion directory watcher


        // ------------------------------------------------------------------------
        // files used in any loaded project
        // ------------------------------------------------------------------------
        Dictionary<string, object> _FileToProjectCache = new Dictionary<string, object>();

        public void AddFileToProjectCache(IList<string> fileList, object project)
        {
            lock (_FileToProjectCache)
            {
                foreach (string file in fileList)
                    _FileToProjectCache[file.ToLower()] = project;
            }
        }

        public void RemoveFileFromProjectCache(IList<string> fileList)
        {
            lock (_FileToProjectCache)
            {
                foreach (string file in fileList)
                    _FileToProjectCache.Remove(file.ToLower());
            }
        }

        public void ClearFileToProjectCache()
        {
            lock (_FileToProjectCache)
            {
                _FileToProjectCache.Clear();
            }
        }

        public int FileProjectMapCacheCount()
        {
            return _FileToProjectCache.Count;
        }
    }
}
