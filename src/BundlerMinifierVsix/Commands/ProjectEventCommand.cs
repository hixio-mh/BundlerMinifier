using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BundlerMinifier;
using EnvDTE;
using EnvDTE80;

namespace BundlerMinifierVsix.Commands
{
    class ProjectEventCommand
    {
        private IServiceProvider _provider;
        private Dictionary<Project, FileSystemWatcher> _listeners;
        private SolutionEvents _events;
        private string[] _ignorePatterns = { "\\node_modules\\", "\\bower_components\\", "\\jspm_packages\\" };
        Timer _timer;
        private ConcurrentDictionary<string, QueueItem> _queue = new ConcurrentDictionary<string, QueueItem>();

        private ProjectEventCommand(IServiceProvider provider)
        {
            _provider = provider;
            _listeners = new Dictionary<Project, FileSystemWatcher>();

            var dte = (DTE2)provider.GetService(typeof(DTE));
            _events = dte.Events.SolutionEvents;

            _events.Opened += OnSolutionOpened;
            _events.AfterClosing += OnSolutionClosing;
            _events.ProjectAdded += EnsureProjectIsActive;
            _events.ProjectRemoved += OnProjectRemoved;

            _timer = new Timer(TimerElapsed, null, 0, 250);
        }

        public static ProjectEventCommand Instance { get; private set; }

        public static void Initialize(IServiceProvider provider)
        {
            Instance = new ProjectEventCommand(provider);
        }

        private void OnSolutionOpened()
        {
            var projects = ProjectHelpers.GetAllProjects();

            foreach (var project in projects)
            {
                EnsureProjectIsActive(project);
            }
        }

        private void OnSolutionClosing()
        {
            for (int i = _listeners.Count - 1; i >= 0; i--)
            {
                OnProjectRemoved(_listeners.ElementAt(i).Key);
            }
        }

        /// <summary>Starts the file system watcher on the project root folder if it isn't already running.</summary>
        public void EnsureProjectIsActive(Project project)
        {
            if (project == null || _listeners.ContainsKey(project))
                return;

            var config = project.GetConfigFile();

            try
            {
                if (!string.IsNullOrEmpty(config) && File.Exists(config))
                {
                    var fsw = new FileSystemWatcher(project.GetRootFolder());
                    fsw.Changed += FileChanged;
                    fsw.IncludeSubdirectories = true;
                    fsw.NotifyFilter = NotifyFilters.Size | NotifyFilters.CreationTime;
                    fsw.EnableRaisingEvents = true;

                    _listeners.Add(project, fsw);
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex);
            }
        }

        private void OnProjectRemoved(Project project)
        {
            if (project == null || !_listeners.ContainsKey(project))
                return;

            _listeners[project].Dispose();
            _listeners.Remove(project);
        }

        void FileChanged(object sender, FileSystemEventArgs e)
        {
            string fileName = Path.GetFileName(e.FullPath);

            // VS adds ~ to temp file names so let's ignore those
            if (fileName.Contains('~') || fileName.Contains(".min."))
                return;

            string file = e.FullPath.ToLowerInvariant();

            if (!BundleFileProcessor.IsSupported(e.FullPath) || _ignorePatterns.Any(p => file.Contains(p)))
                return;

            var project = _listeners.Keys.FirstOrDefault(p => e.FullPath.StartsWith(p.GetRootFolder()));

            if (project != null)
            {
                string configFile = project.GetConfigFile();
                _queue[e.FullPath] = new QueueItem { ConfigFile = configFile };
            }
        }

        void TimerElapsed(object state)
        {
            var items = _queue.Where(i => i.Value.Timestamp < DateTime.Now.AddMilliseconds(-250));

            foreach (var item in items)
            {
                BundleService.SourceFileChanged(item.Value.ConfigFile, item.Key);
            }

            foreach (var item in _queue)
            {
                QueueItem old;
                if (item.Value.Timestamp < DateTime.Now.AddMilliseconds(-250))
                {
                    _queue.TryRemove(item.Key, out old);
                }
            }
        }

        class QueueItem
        {
            public DateTime Timestamp { get; set; } = DateTime.Now;
            public string ConfigFile { get; set; }
        }
    }
}