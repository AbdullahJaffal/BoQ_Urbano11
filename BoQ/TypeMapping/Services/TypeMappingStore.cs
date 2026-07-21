using System;
using System.Collections.Generic;
using Teigha.DatabaseServices;
using UrbanoMetraj.BoQ.TypeMapping.Models;

namespace UrbanoMetraj.BoQ.TypeMapping.Services
{
    /// <summary>
    /// Session cache in front of <see cref="TypeMappingNodManager"/>. NOD reads need a
    /// transaction, so callers must not hit the DWG per-pipe during parsing — load once
    /// per DWG open via <see cref="LoadFromDwg"/>, then look up from memory.
    /// </summary>
    internal static class TypeMappingStore
    {
        private static Dictionary<string, PipeTypeLink>    _pipeLinks;
        private static Dictionary<string, ManholeTypeLink> _manholeLinks;
        private static bool _loaded;

        public static void LoadFromDwg(Database db)
        {
            _pipeLinks = new Dictionary<string, PipeTypeLink>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in TypeMappingNodManager.LoadAllPipeLinks(db))
                _pipeLinks[l.UrbanoCatalogItemGuid] = l;

            _manholeLinks = new Dictionary<string, ManholeTypeLink>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in TypeMappingNodManager.LoadAllManholeLinks(db))
                _manholeLinks[l.UrbanoCatalogItemGuid] = l;

            _loaded = true;
        }

        /// <summary>Forces the next lookup-based caller to reload (e.g. after the DWG changes underneath).</summary>
        public static void Invalidate() => _loaded = false;

        public static PipeTypeLink FindPipeLink(string urbanoCatalogItemGuid)
        {
            if (!_loaded || string.IsNullOrEmpty(urbanoCatalogItemGuid)) return null;
            return _pipeLinks.TryGetValue(urbanoCatalogItemGuid, out var link) ? link : null;
        }

        public static ManholeTypeLink FindManholeLink(string urbanoCatalogItemGuid)
        {
            if (!_loaded || string.IsNullOrEmpty(urbanoCatalogItemGuid)) return null;
            return _manholeLinks.TryGetValue(urbanoCatalogItemGuid, out var link) ? link : null;
        }

        public static IReadOnlyCollection<PipeTypeLink> AllPipeLinks
            => _loaded ? (IReadOnlyCollection<PipeTypeLink>)_pipeLinks.Values : Array.Empty<PipeTypeLink>();

        public static IReadOnlyCollection<ManholeTypeLink> AllManholeLinks
            => _loaded ? (IReadOnlyCollection<ManholeTypeLink>)_manholeLinks.Values : Array.Empty<ManholeTypeLink>();

        public static void SavePipeLink(Database db, PipeTypeLink link)
        {
            TypeMappingNodManager.SavePipeLink(db, link);
            if (_loaded) _pipeLinks[link.UrbanoCatalogItemGuid] = link;
        }

        public static void SaveManholeLink(Database db, ManholeTypeLink link)
        {
            TypeMappingNodManager.SaveManholeLink(db, link);
            if (_loaded) _manholeLinks[link.UrbanoCatalogItemGuid] = link;
        }
    }
}
