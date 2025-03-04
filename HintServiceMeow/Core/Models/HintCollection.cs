﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HintServiceMeow.Core.Models.Hints;

namespace HintServiceMeow.Core.Models
{
    /// <summary>
    /// The collection of hints. This class is used to store and manage hints in PlayerDisplay.
    /// HintList are used for API and HintGroup are used for internal usage.
    /// </summary>
    public class HintCollection
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<AbstractHint, byte>> _hintGroups = new ConcurrentDictionary<string, ConcurrentDictionary<AbstractHint, byte>>();

        public List<List<AbstractHint>> AllGroups
        {
            get
            {
                return _hintGroups.Values.Select(x => x.Keys.ToList()).ToList();
            }
        }

        internal void AddHint(string assemblyName, AbstractHint hint)
        {
            _hintGroups.GetOrAdd(assemblyName, new ConcurrentDictionary<AbstractHint, byte>()).TryAdd(hint ?? throw new ArgumentNullException("hint"), 0);
        }

        internal void RemoveHint(string assemblyName, AbstractHint hint)
        {
            if (!_hintGroups.TryGetValue(assemblyName, out var hintDict))
                return;

            hintDict.TryRemove(hint ?? throw new ArgumentNullException("hint"), out _);
        }

        internal void RemoveHint(string assemblyName, Func<AbstractHint, bool> predicate)
        {
            if (!_hintGroups.TryGetValue(assemblyName, out var hintDict))
                return;

            foreach(var hint in hintDict.Keys.Where(predicate).ToList())
                hintDict.TryRemove(hint, out _);
        }

        internal void ClearHints(string assemblyName)
        {
            if (!_hintGroups.TryGetValue(assemblyName, out var hintDict))
                return;

            hintDict.Clear();
        }

        public IEnumerable<AbstractHint> GetHints(string assemblyName)
        {
            return _hintGroups.GetOrAdd(assemblyName, new ConcurrentDictionary<AbstractHint, byte>()).Select(x => x.Key);
        }

        public IEnumerable<AbstractHint> GetHints(string assemblyName, Func<AbstractHint, bool> predicate)
        {
            return GetHints(assemblyName).Where(predicate);
        }
    }
}
