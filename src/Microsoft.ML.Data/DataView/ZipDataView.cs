// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.Runtime.Internal.Utilities;

namespace Microsoft.ML.Runtime.Data
{
    /// <summary>
    /// This is a data view that is a 'zip' of several data views.
    /// The length of the zipped data view is equal to the shortest of the lengths of the components. 
    /// </summary>
    public sealed class ZipDataView : IDataView
    {
        // REVIEW: there are other potential 'zip modes' that can be implemented:
        // * 'zip longest', iterate until all sources finish, and return the 'sensible missing values' for sources that ended
        // too early.
        // * 'zip longest with loop', iterate until the longest source finishes, and for those that finish earlier, restart from
        // the beginning.

        public const string RegistrationName = "ZipDataView";

        private readonly IHost _host;
        private readonly IDataView[] _sources;
        private readonly ZipSchema _schema;

        public static IDataView Create(IHostEnvironment env, IEnumerable<IDataView> sources)
        {
            Contracts.CheckValue(env, nameof(env));
            var host = env.Register(RegistrationName);
            host.CheckValue(sources, nameof(sources));

            var srcArray = sources.ToArray();
            host.CheckNonEmpty(srcArray, nameof(sources));
            if (srcArray.Length == 1)
                return srcArray[0];
            return new ZipDataView(host, srcArray);
        }

        private ZipDataView(IHost host, IDataView[] sources)
        {
            Contracts.AssertValue(host);
            _host = host;

            _host.Assert(Utils.Size(sources) > 1);
            _sources = sources;
            _schema = new ZipSchema(_sources.Select(x => x.Schema).ToArray());
        }

        public bool CanShuffle { get { return false; } }

        public ISchema Schema { get { return _schema; } }

        public long? GetRowCount(bool lazy = true)
        {
            long min = -1;
            foreach (var source in _sources)
            {
                var cur = source.GetRowCount(lazy);
                if (cur == null)
                    return null;
                _host.Check(cur.Value >= 0, "One of the sources returned a negative row count");
                if (min < 0 || min > cur.Value)
                    min = cur.Value;
            }

            return min;
        }

        public IRowCursor GetRowCursor(Func<int, bool> predicate, IRandom rand = null)
        {
            _host.CheckValue(predicate, nameof(predicate));
            _host.CheckValueOrNull(rand);

            var srcPredicates = _schema.GetInputPredicates(predicate);

            // REVIEW: if we know the row counts, we could only open cursor if it has needed columns, and have the 
            // outer cursor handle the early stopping. If we don't know row counts, we need to open all the cursors because
            // we don't know which one will be the shortest.
            // One reason this is not done currently is because the API has 'somewhat mutable' data views, so potentially this
            // optimization might backfire.
            var srcCursors = _sources
                .Select((dv, i) => srcPredicates[i] == null ? GetMinimumCursor(dv) : dv.GetRowCursor(srcPredicates[i], null)).ToArray();
            return new Cursor(this, srcCursors, predicate);
        }

        /// <summary>
        /// Create an <see cref="IRowCursor"/> with no requested columns on a data view. 
        /// Potentially, this can be optimized by calling GetRowCount(lazy:true) first, and if the count is not known, 
        /// wrapping around GetCursor().
        /// </summary>
        private IRowCursor GetMinimumCursor(IDataView dv)
        {
            _host.AssertValue(dv);
            return dv.GetRowCursor(x => false);
        }

        public IRowCursor[] GetRowCursorSet(out IRowCursorConsolidator consolidator, Func<int, bool> predicate, int n, IRandom rand = null)
        {
            consolidator = null;
            return new IRowCursor[] { GetRowCursor(predicate, rand) };
        }

        /// <summary>
        /// This is a result of appending several schema together.
        /// </summary>
        internal sealed class ZipSchema : ISchema
        {
            private readonly ISchema[] _sources;
            // Zero followed by cumulative column counts.
            private readonly int[] _cumulativeColCounts;

            public ZipSchema(ISchema[] sources)
            {
                Contracts.AssertNonEmpty(sources);
                _sources = sources;
                _cumulativeColCounts = new int[_sources.Length + 1];
                _cumulativeColCounts[0] = 0;

                for (int i = 0; i < sources.Length; i++)
                {
                    var schema = sources[i];
                    _cumulativeColCounts[i + 1] = _cumulativeColCounts[i] + schema.ColumnCount;
                }
            }

            /// <summary>
            /// Returns an array of input predicated for sources, corresponding to the input predicate.
            /// The returned array size is equal to the number of sources, but if a given source is not needed at all, 
            /// the corresponding predicate will be null.
            /// </summary>
            public Func<int, bool>[] GetInputPredicates(Func<int, bool> predicate)
            {
                Contracts.AssertValue(predicate);
                var result = new Func<int, bool>[_sources.Length];
                for (int i = 0; i < _sources.Length; i++)
                {
                    var lastColCount = _cumulativeColCounts[i];
                    result[i] = srcCol => predicate(srcCol + lastColCount);
                }

                return result;
            }

            /// <summary>
            /// Checks whether the column index is in range.
            /// </summary>
            public void CheckColumnInRange(int col)
            {
                Contracts.CheckParam(0 <= col && col < _cumulativeColCounts[_cumulativeColCounts.Length - 1], nameof(col), "Column index out of range");
            }

            public void GetColumnSource(int col, out int srcIndex, out int srcCol)
            {
                CheckColumnInRange(col);
                if (!_cumulativeColCounts.TryFindIndexSorted(0, _cumulativeColCounts.Length, col, out srcIndex))
                    srcIndex--;
                Contracts.Assert(0 <= srcIndex && srcIndex < _cumulativeColCounts.Length);
                srcCol = col - _cumulativeColCounts[srcIndex];
                Contracts.Assert(0 <= srcCol && srcCol < _sources[srcIndex].ColumnCount);
            }

            public int ColumnCount { get { return _cumulativeColCounts[_cumulativeColCounts.Length - 1]; } }

            public bool TryGetColumnIndex(string name, out int col)
            {
                for (int i = _sources.Length; --i >= 0; )
                {
                    if (_sources[i].TryGetColumnIndex(name, out col))
                    {
                        col += _cumulativeColCounts[i];
                        return true;
                    }
                }

                col = -1;
                return false;
            }

            public string GetColumnName(int col)
            {
                int dv;
                int srcCol;
                GetColumnSource(col, out dv, out srcCol);
                return _sources[dv].GetColumnName(srcCol);
            }

            public ColumnType GetColumnType(int col)
            {
                int dv;
                int srcCol;
                GetColumnSource(col, out dv, out srcCol);
                return _sources[dv].GetColumnType(srcCol);
            }

            public IEnumerable<KeyValuePair<string, ColumnType>> GetMetadataTypes(int col)
            {
                int dv;
                int srcCol;
                GetColumnSource(col, out dv, out srcCol);
                return _sources[dv].GetMetadataTypes(srcCol);
            }

            public ColumnType GetMetadataTypeOrNull(string kind, int col)
            {
                int dv;
                int srcCol;
                GetColumnSource(col, out dv, out srcCol);
                return _sources[dv].GetMetadataTypeOrNull(kind, srcCol);
            }

            public void GetMetadata<TValue>(string kind, int col, ref TValue value)
            {
                int dv;
                int srcCol;
                GetColumnSource(col, out dv, out srcCol);
                _sources[dv].GetMetadata(kind, srcCol, ref value);
            }
        }

        private sealed class Cursor : RootCursorBase, IRowCursor
        {
            private readonly IRowCursor[] _cursors;
            private readonly ZipSchema _schema;
            private readonly bool[] _isColumnActive;

            public override long Batch { get { return 0; } }

            public Cursor(ZipDataView parent, IRowCursor[] srcCursors, Func<int, bool> predicate)
                : base(parent._host)
            {
                Ch.AssertNonEmpty(srcCursors);
                Ch.AssertValue(predicate);

                _cursors = srcCursors;
                _schema = parent._schema;
                _isColumnActive = Utils.BuildArray(_schema.ColumnCount, predicate);
            }

            public override void Dispose()
            {
                for (int i = _cursors.Length - 1; i >= 0; i--)
                    _cursors[i].Dispose();
                base.Dispose();
            }

            public override ValueGetter<UInt128> GetIdGetter()
            {
                return
                    (ref UInt128 val) =>
                    {
                        Ch.Check(IsGood, "Cannot call ID getter in current state");
                        val = new UInt128((ulong)Position, 0);
                    };
            }

            protected override bool MoveNextCore()
            {
                Ch.Assert(State != CursorState.Done);
                foreach (var cursor in _cursors)
                {
                    Ch.Assert(cursor.State != CursorState.Done);
                    if (!cursor.MoveNext())
                        return false;
                }

                return true;
            }

            protected override bool MoveManyCore(long count)
            {
                Ch.Assert(State != CursorState.Done);
                foreach (var cursor in _cursors)
                {
                    Ch.Assert(cursor.State != CursorState.Done);
                    if (!cursor.MoveMany(count))
                        return false;
                }

                return true;
            }

            public ISchema Schema { get { return _schema; } }

            public bool IsColumnActive(int col)
            {
                _schema.CheckColumnInRange(col);
                return _isColumnActive[col];
            }

            public ValueGetter<TValue> GetGetter<TValue>(int col)
            {
                int dv;
                int srcCol;
                _schema.GetColumnSource(col, out dv, out srcCol);
                return _cursors[dv].GetGetter<TValue>(srcCol);
            }
        }
    }
}