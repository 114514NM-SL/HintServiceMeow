﻿using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;

using Mirror;
using MEC;

using HintServiceMeow.Core.Enum;
using HintServiceMeow.Core.Models.Hints;
using HintServiceMeow.Core.Models;
using HintServiceMeow.Core.Utilities.Parser;
using HintServiceMeow.Core.Interface;
using Utils.NonAllocLINQ;
using Exiled.API.Features;

namespace HintServiceMeow.Core.Utilities
{
    /// <summary>
    /// Represent a player's display. This class is used to manage hints and update hint to player's display
    /// </summary>
    public class PlayerDisplay
    {
        /// <summary>
        /// The player this instance bind to
        /// </summary>
        public ReferenceHub ReferenceHub { get; }
        public NetworkConnection ConnectionToClient { get; }

        /// <summary>
        /// Invoke every frame when ReferenceHub display is ready to update.
        /// </summary>
        public event UpdateAvailableEventHandler UpdateAvailable;
        public delegate void UpdateAvailableEventHandler(UpdateAvailableEventArg ev);

        private static readonly object PlayerDisplayListLock = new object();
        private static readonly HashSet<PlayerDisplay> PlayerDisplayList = new HashSet<PlayerDisplay>();

        private readonly ConcurrentBag<IDisplayOutput> _displayOutputs = new ConcurrentBag<IDisplayOutput> { new DefaultDisplayOutput() };

        private readonly HintCollection _hints = new HintCollection();
        private readonly TaskScheduler _taskScheduler;
        private IHintParser _hintParser = new HintParser();
        private ICompatibilityAdaptor _adapter;

        private static readonly CoroutineHandle _updateCoroutine;
        private static readonly CoroutineHandle _coroutine;

        private Task<string> _currentParserTask;
        private readonly object _currentParserTaskLock = new object();
        
        #region Constructor and Destructors

        static PlayerDisplay()
        {
            _updateCoroutine = Timing.RunCoroutine(UpdateCoroutineMethod());
            _coroutine = Timing.RunCoroutine(CoroutineMethod());
        }

        private PlayerDisplay(ReferenceHub referenceHub)
        {
            this.ConnectionToClient = referenceHub.netIdentity.connectionToClient;
            this.ReferenceHub = referenceHub;

            this._taskScheduler = new TaskScheduler(TimeSpan.FromSeconds(0.5f), () =>
            {
                StartParserTask();
                _taskScheduler?.PauseAction();//Pause action until the parser task is done
            });
            this._adapter = new CompatibilityAdaptor(this);

            lock (PlayerDisplayListLock)
            {
                PlayerDisplayList.Add(this);
            }
        }

        internal static void Destruct(ReferenceHub referenceHub)
        {
            lock (PlayerDisplayListLock)
            {
                PlayerDisplayList.RemoveWhere(x => x.ReferenceHub == referenceHub);
            }
        }

        internal static void ClearInstance()
        {
            lock (PlayerDisplayListLock)
            {
                PlayerDisplayList.Clear();
            }
        }

        #endregion

        #region Properties

        public ConcurrentBag<IDisplayOutput> DisplayOutputs => _displayOutputs;

        public IHintParser HintParser
        {
            get => _hintParser;
            set => _hintParser = value ?? throw new ArgumentNullException(nameof(value));
        }

        public ICompatibilityAdaptor CompatibilityAdaptor
        {
            get => _adapter;
            set => _adapter = value ?? throw new ArgumentNullException(nameof(value));
        }

        #endregion

        #region Coroutine Methods
        private static IEnumerator<float> UpdateCoroutineMethod()
        {
            while (true)
            {
                yield return Timing.WaitForOneFrame;

                lock (PlayerDisplayListLock)
                {
                    foreach (var pd in PlayerDisplayList)
                    {
                        lock (pd._currentParserTaskLock)
                        {
                            if (pd._currentParserTask is null || !pd._currentParserTask.IsCompleted)
                                continue;

                            try
                            {
                                pd.SendHint(pd._currentParserTask.Result);

                                pd._taskScheduler.ResumeAction();

                                pd._currentParserTask.Dispose();
                                pd._currentParserTask = null;
                            }
                            catch (Exception ex)
                            {
                                PluginAPI.Core.Log.Error(ex.ToString());
                            }

                            yield return Timing.WaitForOneFrame;
                        }
                    }
                }
            }
        }

        private static IEnumerator<float> CoroutineMethod()
        {
            while (true)
            {
                yield return Timing.WaitForSeconds(0.1f);

                lock (PlayerDisplayListLock)
                {
                    foreach (var pd in PlayerDisplayList)
                    {
                        if (pd._taskScheduler.IsReadyForNextAction())
                        {
                            try
                            {
                                pd.UpdateAvailable?.Invoke(new UpdateAvailableEventArg(pd));
                            }
                            catch (Exception ex)
                            {
                                PluginAPI.Core.Log.Error(ex.ToString());
                            }
                        }

                        //Periodic update
                        if (pd._taskScheduler.LastActionStopwatch.Elapsed > TimeSpan.FromSeconds(5) && pd._currentParserTask == null)
                            pd.ScheduleUpdate();
                    }
                }
            }
        }
        #endregion

        #region Update Management
        internal void OnHintUpdate(AbstractHint hint)
        {
            if (hint.SyncSpeed == HintSyncSpeed.UnSync)
                return;

            var maxWaitingTime = hint.SyncSpeed switch
            {
                HintSyncSpeed.Fastest => 0,
                HintSyncSpeed.Fast => 0.1f,
                HintSyncSpeed.Normal => 0.3f,
                HintSyncSpeed.Slow => 1f,
                HintSyncSpeed.Slowest => 3f,
                _ => throw new ArgumentOutOfRangeException()
            };

            ScheduleUpdate(maxWaitingTime, hint);
            
        }

        private void ScheduleUpdate(float maxWaitingTime = float.MinValue, AbstractHint updatingHint = null)
        {
            lock(_currentParserTaskLock)
            {
                if (_currentParserTask is not null)
                    return;
            }

            if (maxWaitingTime <= 0)
            {
                _taskScheduler.StartAction();
                return;
            }

            var predictingHints = _hints.AllGroups.SelectMany(x => x);

            if(updatingHint != null)
            {
                predictingHints = predictingHints.Where(h => h.SyncSpeed >= updatingHint.SyncSpeed && h != updatingHint);
            }

            var maxWaitingTimeSpan = TimeSpan.FromSeconds(maxWaitingTime);
            var now = DateTime.Now;

            DateTime predictedUpdatingTime = predictingHints
                .Select(h => h.UpdateAnalyser.EstimateNextUpdate())
                .Where(x => x - now >= TimeSpan.Zero && x - now <= maxWaitingTimeSpan)
                .DefaultIfEmpty(now)
                .Max();

            var delay = (float)(predictedUpdatingTime - now).TotalSeconds;

            if(delay <= 0)
                _taskScheduler.StartAction();
            else
                _taskScheduler.StartAction(delay, TaskScheduler.DelayType.Fastest);
        }

        /// <summary>
        /// Force an update when the update is available. You do not have to use this method unless you are using UnSync hints
        /// </summary>
        public void ForceUpdate(bool useFastUpdate = false)
        {
            ScheduleUpdate(useFastUpdate ? 0f : 0.3f);
        }
        #endregion

        #region Update Methods
        private void StartParserTask()
        {
            if (_currentParserTask is not null)
                return;

            lock (_currentParserTaskLock)
                _currentParserTask = Task.Run(() => _hintParser.ParseToMessage(_hints));
        }

        private void SendHint(string text)
        {
            foreach(var output in _displayOutputs)
            {
                try
                {
                    output.ShowHint(new Models.Arguments.DisplayOutputArg(this, text));
                }
                catch(Exception ex)
                {
                    PluginAPI.Core.Log.Error(ex.ToString());
                }
            }
        }
        #endregion

        #region Player Display Management Methods

        /// <summary>
        /// Get the PlayerDisplay instance of the player. If the instance have not been created yet, then it will create one.
        /// Not Thread Safe
        /// </summary>
        public static PlayerDisplay Get(ReferenceHub referenceHub)
        {
            if (referenceHub is null)
                throw new ArgumentNullException(nameof(referenceHub));

            lock (PlayerDisplayListLock)
            {
                return PlayerDisplayList.FirstOrDefault(x => x.ReferenceHub == referenceHub) ?? new PlayerDisplay(referenceHub);
            }
    }

        /// <summary>
        /// Get the PlayerDisplay instance of the player. If the instance have not been created yet, then it will create one.
        /// Not Thread Safe
        /// </summary>
        public static PlayerDisplay Get(PluginAPI.Core.Player player)
        {
            if (player is null)
                throw new ArgumentNullException(nameof(player));

            return Get(player.ReferenceHub);
        }

#if EXILED
        /// <summary>
        /// Get the PlayerDisplay instance of the player. If the instance have not been created yet, then it will create one.
        /// Not Thread Safe
        /// </summary>
        public static PlayerDisplay Get(Exiled.API.Features.Player player)
        {
            if(player is null)
                throw new ArgumentNullException(nameof(player));

            return Get(player.ReferenceHub);
        }
#endif

        #endregion

        #region Hint Management Methods

        public void AddHint(AbstractHint hint)
        {
            if (hint == null)
                return;

            this.InternalAddHint(Assembly.GetCallingAssembly().FullName, hint);
        }

        public void AddHint(IEnumerable<AbstractHint> hints)
        {
            if (hints == null)
                return;

            this.InternalAddHint(Assembly.GetCallingAssembly().FullName, hints);
        }

        public void RemoveHint(AbstractHint hint)
        {
            if (hint == null)
                return;

            this.InternalRemoveHint(Assembly.GetCallingAssembly().FullName, hint);
        }

        public void RemoveHint(IEnumerable<AbstractHint> hints)
        {
            if (hints == null)
                return;

            this.InternalRemoveHint(Assembly.GetCallingAssembly().FullName, hints);
        }

        public void RemoveHint(string id)
        {
            if (id is null)
                throw new ArgumentNullException(nameof(id));

            if (id == string.Empty)
                throw new ArgumentException("A empty string had been passed to RemoveHint");

            this.InternalRemoveHint(Assembly.GetCallingAssembly().FullName, id);
        }

        public void RemoveHint(Guid id)
        {
            this.InternalRemoveHint(Assembly.GetCallingAssembly().FullName, id);
        }

        public void ClearHint()
        {
            this.InternalClearHint(Assembly.GetCallingAssembly().FullName);
        }

        public AbstractHint GetHint(string id)
        {
            if (id is null)
                throw new ArgumentNullException(nameof(id));

            if (id == string.Empty)
                throw new ArgumentException("A empty string had been passed to GetHint");

            return _hints.GetHints(Assembly.GetCallingAssembly().FullName, x => x.Id == id).FirstOrDefault();
        }

        public AbstractHint GetHint(Guid id)
        {
            return _hints.GetHints(Assembly.GetCallingAssembly().FullName, x => x.Guid == id).FirstOrDefault();
        }

        internal void InternalAddHint(string name, AbstractHint hint, bool update = true)
        {
            hint.HintUpdated += OnHintUpdate;
            UpdateAvailable += hint.TryUpdateHint;

            _hints.AddHint(name, hint);

            if (update)
                ScheduleUpdate();
        }

        internal void InternalAddHint(string name, IEnumerable<AbstractHint> hints, bool update = true)
        {
            foreach (var hint in hints)
            {
                hint.HintUpdated += OnHintUpdate;
                UpdateAvailable += hint.TryUpdateHint;

                _hints.AddHint(name, hint);
            }

            if (update)
                ScheduleUpdate();
        }

        internal void InternalRemoveHint(string name, AbstractHint hint, bool update = true)
        {
            hint.HintUpdated -= OnHintUpdate;
            UpdateAvailable -= hint.TryUpdateHint;

            _hints.RemoveHint(name, hint);

            if (update)
                ScheduleUpdate();
        }

        internal void InternalRemoveHint(string name, IEnumerable<AbstractHint> hints, bool update = true)
        {
            foreach (var hint in hints)
            {
                hint.HintUpdated -= OnHintUpdate;
                UpdateAvailable -= hint.TryUpdateHint;

                _hints.RemoveHint(name, hint);
            }

            if (update)
                ScheduleUpdate();
        }

        internal void InternalRemoveHint(string name, Guid id, bool update = true)
        {
            var hint = _hints.GetHints(name).FirstOrDefault(x => x.Id.Equals(id));

            if (hint == null)
                return;

            hint.HintUpdated -= OnHintUpdate;
            UpdateAvailable -= hint.TryUpdateHint;

            _hints.RemoveHint(name, x => x.Id.Equals(id));

            if (update)
                ScheduleUpdate();
        }

        internal void InternalRemoveHint(string name, string id, bool update = true)
        {
            var hint = _hints.GetHints(name).FirstOrDefault(x => x.Id.Equals(id));

            if (hint == null)
                return;

            hint.HintUpdated -= OnHintUpdate;
            UpdateAvailable -= hint.TryUpdateHint;

            _hints.RemoveHint(name, x => x.Id.Equals(id));

            if (update)
                ScheduleUpdate();
        }

        internal void InternalClearHint(string name, bool update = true)
        {
            foreach(var hint in _hints.GetHints(name).ToList())
            {
                hint.HintUpdated -= OnHintUpdate;
                UpdateAvailable -= hint.TryUpdateHint;
            }

            _hints.ClearHints(name);

            if (update)
                ScheduleUpdate();
        }
        #endregion

        internal void ShowCompatibilityHint(string assemblyName, string content, float duration) => this._adapter.ShowHint(new Models.Arguments.CompatibilityAdaptorArg(assemblyName, content, duration));

        /// <summary>
        /// Argument for UpdateAvailable Event
        /// </summary>
        public class UpdateAvailableEventArg
        {
            public PlayerDisplay PlayerDisplay { get; set; }

            internal UpdateAvailableEventArg(PlayerDisplay playerDisplay)
            {
                this.PlayerDisplay = playerDisplay;
            }
        }
    }
}
