using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Marten.Events;
using Marten.Patching;
using Marten.Schema;
using Marten.Services.Deletes;
using Marten.Storage;
using Marten.Util;

namespace Marten.Services
{
    public class UnitOfWork: IUnitOfWork
    {
        private readonly ConcurrentDictionary<Guid, EventStream> _events = new ConcurrentDictionary<Guid, EventStream>();

        private readonly DocumentStore _store;
        private readonly ITenant _tenant;

        private readonly IList<IDocumentTracker> _trackers = new List<IDocumentTracker>();

        private readonly Ref<ImHashMap<Type, IList<IStorageOperation>>> _operations =
            Ref.Of(ImHashMap<Type, IList<IStorageOperation>>.Empty);

        private readonly IList<IStorageOperation> _ancillaryOperations = new List<IStorageOperation>();

        public UnitOfWork(DocumentStore store, ITenant tenant)
        {
            _store = store;
            _tenant = tenant;
        }

        public IEnumerable<IDeletion> Deletions()
        {
            return _operations.Value.Enumerate().SelectMany(x => x.Value).OfType<IDeletion>();
        }

        public IEnumerable<IDeletion> DeletionsFor<T>()
        {
            return operationsFor(typeof(T)).OfType<IDeletion>();
        }

        public IEnumerable<IDeletion> DeletionsFor(Type documentType)
        {
            return operationsFor(documentType).OfType<IDeletion>();
        }

        public IEnumerable<object> Updates()
        {
            return _operations.Value.Enumerate().SelectMany(x => x.Value.Where(t => t is UpsertDocument || t is UpdateDocument).OfType<DocumentStorageOperation>().Select(u => u.Document))
                .Union(detectTrackerChanges().Select(x => x.Document));
        }

        public IEnumerable<T> UpdatesFor<T>()
        {
            return Updates().OfType<T>();
        }

        public IEnumerable<object> Inserts()
        {
            return _operations.Value.Enumerate().SelectMany(x => x.Value).OfType<InsertDocument>().Select(x => x.Document);
        }

        public IEnumerable<T> InsertsFor<T>()
        {
            return Inserts().OfType<T>();
        }

        public IEnumerable<T> AllChangedFor<T>()
        {
            return InsertsFor<T>().Union(UpdatesFor<T>());
        }

        public IEnumerable<EventStream> Streams()
        {
            return _events.Values;
        }

        public IEnumerable<PatchOperation> Patches()
        {
            return _operations.Value.Enumerate().SelectMany(x => x.Value).OfType<PatchOperation>();
        }

        public IEnumerable<IStorageOperation> Operations()
        {
            return _operations.Value.Enumerate().SelectMany(x => x.Value);
        }

        public IEnumerable<IStorageOperation> OperationsFor<T>()
        {
            return operationsFor(typeof(T));
        }

        public IEnumerable<IStorageOperation> OperationsFor(Type documentType)
        {
            return operationsFor(documentType);
        }

        public void AddTracker(IDocumentTracker tracker)
        {
            _trackers.Fill(tracker);
        }

        public void RemoveTracker(IDocumentTracker tracker)
        {
            _trackers.Remove(tracker);
        }

        public void StoreStream(EventStream stream)
        {
            _events[stream.Id] = stream;
        }

        public bool HasStream(Guid id)
        {
            return _events.ContainsKey(id);
        }

        public EventStream StreamFor(Guid id)
        {
            return _events[id];
        }

        private IList<IStorageOperation> operationsFor(Type documentType)
        {
            var storageType = _tenant.StorageFor(documentType).TopLevelBaseType;
            if (!_operations.Value.TryFind(storageType, out var value))
            {
                value = new List<IStorageOperation>();
                _operations.Swap(o => o.AddOrUpdate(storageType, value));
            }
            return value;
        }

        public void Patch(PatchOperation patch)
        {
            var list = operationsFor(patch.DocumentType);

            list.Add(patch);
        }

        public void StoreUpserts<T>(params T[] documents)
        {
            var list = operationsFor(typeof(T));

            list.AddRange(documents.Select(x => new UpsertDocument(x)));
        }

        public void StoreUpdates<T>(params T[] documents)
        {
            var list = operationsFor(typeof(T));

            list.AddRange(documents.Select(x => new UpdateDocument(x)));
        }

        public void StoreInserts<T>(params T[] documents)
        {
            var list = operationsFor(typeof(T));

            list.AddRange(documents.Select(x => new InsertDocument(x)));
        }

        public ChangeSet ApplyChanges(UpdateBatch batch)
        {
            var changes = buildChangeSet(batch);

            batch.Execute();

            ClearChanges(changes.Changes);

            return changes;
        }

        private ChangeSet buildChangeSet(UpdateBatch batch)
        {
            var documentChanges = determineChanges(batch);
            var changes = new ChangeSet(documentChanges);

            // TODO -- make these be calculated properties on ChangeSet
            changes.Updated.Fill(Updates());
            changes.Inserted.Fill(Inserts());

            changes.Streams.AddRange(_events.Values);
            changes.Operations.AddRange(_operations.Value.Enumerate().SelectMany(x => x.Value));
            changes.Operations.AddRange(_ancillaryOperations);

            return changes;
        }

        public async Task<ChangeSet> ApplyChangesAsync(UpdateBatch batch, CancellationToken token)
        {
            var changes = buildChangeSet(batch);

            await batch.ExecuteAsync(token).ConfigureAwait(false);

            ClearChanges(changes.Changes);

            return changes;
        }

        private bool shouldSort(List<IStorageOperation> operations, out IComparer<IStorageOperation> comparer)
        {
            comparer = null;
            if (operations.Count <= 1)
                return false;

            if (operations.Select(x => x.DocumentType).Distinct().Count() == 1)
                return false;

            var types = _operations.Value.Enumerate().Select(x => x.Key).TopologicalSort(GetTypeDependencies).ToArray();

            if (operations.OfType<IDeletion>().Any())
            {
                comparer = new StorageOperationWithDeletionsComparer(types);
            }
            else
            {
                comparer = new StorageOperationByTypeComparer(types);
            }

            return true;
        }

        private DocumentChange[] determineChanges(UpdateBatch batch)
        {
            var allOperations = _operations.Value.Enumerate().SelectMany(x => x.Value).ToList();
            if (shouldSort(allOperations, out var comparer))
            {
                allOperations.Sort(comparer);
            }

            foreach (var operation in allOperations)
            {
                // No Virginia, I do not approve of this but I'm pulling all my hair
                // out as is trying to make this work
                if (operation is DocumentStorageOperation)
                {
                    operation.As<DocumentStorageOperation>().Persist(batch, _tenant);
                }
                else
                {
                    batch.Add(operation);
                }
            }

            writeEvents(batch);

            batch.Add(_ancillaryOperations);

            var changes = detectTrackerChanges();
            changes.GroupBy(x => x.DocumentType).Each(group =>
            {
                var upsert = _tenant.StorageFor(group.Key);

                group.Each(c => { upsert.RegisterUpdate(null, UpdateStyle.Upsert, batch, c.Document, c.Json); });
            });

            return changes;
        }

        private void writeEvents(UpdateBatch batch)
        {
            var upsert = new EventStreamAppender(_store.Events);
            _events.Values.Each(stream =>
            {
                upsert.RegisterUpdate(batch, stream);
            });
        }

        private IEnumerable<Type> GetTypeDependencies(Type type)
        {
            var mappingFor = _tenant.MappingFor(type);
            var documentMapping = mappingFor as DocumentMapping ?? (mappingFor as SubClassMapping)?.Parent;
            if (documentMapping == null)
                return Enumerable.Empty<Type>();

            return documentMapping.ForeignKeys.Where(x => x.ReferenceDocumentType != type && x.ReferenceDocumentType != null)
                .SelectMany(keyDefinition =>
                {
                    var results = new List<Type>();
                    var referenceMappingType =
                        _tenant.MappingFor(keyDefinition.ReferenceDocumentType) as DocumentMapping;
                    // If the reference type has sub-classes, also need to insert/update them first too
                    if (referenceMappingType != null && referenceMappingType.SubClasses.Any())
                    {
                        results.AddRange(referenceMappingType.SubClasses.Select(s => s.DocumentType));
                    }
                    results.Add(keyDefinition.ReferenceDocumentType);
                    return results;
                });
        }

        private DocumentChange[] detectTrackerChanges()
        {
            return _trackers.SelectMany(x => x.DetectChanges()).ToArray();
        }

        public void Add(IStorageOperation operation)
        {
            if (operation.DocumentType == null)
            {
                _ancillaryOperations.Add(operation);
            }
            else
            {
                var list = operationsFor(operation.DocumentType);
                list.Add(operation);
            }
        }

        private void ClearChanges(DocumentChange[] changes)
        {
            _operations.Swap(o => ImHashMap<Type, IList<IStorageOperation>>.Empty);
            _events.Clear();
            changes.Each(x => x.ChangeCommitted());
        }

        public bool HasAnyUpdates()
        {
            return Updates().Any() || _events.Any() || _operations.Value.Enumerate().Any() || _ancillaryOperations.Any();
        }

        public bool Contains<T>(T entity)
        {
            return _operations.Value.Enumerate().SelectMany(x => x.Value.OfType<DocumentStorageOperation>()).Any(x => object.ReferenceEquals(entity, x.Document));
        }

        public IEnumerable<T> NonDocumentOperationsOf<T>() where T : IStorageOperation
        {
            return _ancillaryOperations.OfType<T>();
        }

        public bool HasStream(string stream)
        {
            return _events.Values.Any(x => x.Key == stream);
        }

        public EventStream StreamFor(string stream)
        {
            return _events.Values.First(x => x.Key == stream);
        }

        public void Eject<T>(T document)
        {
            var operations = operationsFor(typeof(T));
            var matching = operations.OfType<DocumentStorageOperation>().Where(x => object.ReferenceEquals(document, x.Document)).ToArray();

            foreach (var operation in matching)
            {
                operations.Remove(operation);
            }
        }

        private class StorageOperationWithDeletionsComparer: IComparer<IStorageOperation>
        {
            private readonly Type[] _topologicallyOrderedTypes;

            public StorageOperationWithDeletionsComparer(Type[] topologicallyOrderedTypes)
            {
                _topologicallyOrderedTypes = topologicallyOrderedTypes;
            }

            public int Compare(IStorageOperation x, IStorageOperation y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                if (x?.DocumentType == null || y?.DocumentType == null)
                {
                    return 0;
                }

                // Maintain order if same document type and same operation
                if (x.DocumentType == y.DocumentType && x.GetType() == y.GetType())
                {
                    return 0;
                }

                var xIndex = FindIndex(x);
                var yIndex = FindIndex(y);

                var xIsDelete = x is DeleteWhere || x is DeleteById;
                var yIsDelete = y is DeleteWhere || y is DeleteById;

                if (xIsDelete != yIsDelete)
                {
                    // Arbitrary order if one is a delete but the other is not, because this will force the sorting
                    // to try and compare these documents against others and fall in to the below checks.
                    return -1;
                }

                if (xIsDelete)
                {
                    // Both are deletes, so we need reverse topological order to inserts, updates and upserts
                    return yIndex.CompareTo(xIndex);
                }

                // Both are inserts, updates or upserts so topological
                return xIndex.CompareTo(yIndex);
            }

            private int FindIndex(IStorageOperation x)
            {
                // Will loop through up the inheritance chain until reaches the end or the index is found, used
                // to handle inheritance as topologically sorted array may not have the subclasses listed
                var documentType = x.DocumentType;
                var index = 0;

                do
                {
                    index = _topologicallyOrderedTypes.IndexOf(documentType);
                    documentType = documentType.BaseType;
                } while (index == -1 && documentType != null);

                return index;
            }
        }

        private class StorageOperationByTypeComparer: IComparer<IStorageOperation>
        {
            private readonly Type[] _topologicallyOrderedTypes;

            public StorageOperationByTypeComparer(Type[] topologicallyOrderedTypes)
            {
                _topologicallyOrderedTypes = topologicallyOrderedTypes;
            }

            public int Compare(IStorageOperation x, IStorageOperation y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                if (x?.DocumentType == null || y?.DocumentType == null)
                {
                    return 0;
                }

                if (x.DocumentType == y.DocumentType)
                {
                    return 0;
                }

                var xIndex = FindIndex(x);
                var yIndex = FindIndex(y);

                return xIndex.CompareTo(yIndex);
            }

            private int FindIndex(IStorageOperation x)
            {
                // Will loop through up the inheritance chain until reaches the end or the index is found, used
                // to handle inheritance as topologically sorted array may not have the subclasses listed
                var documentType = x.DocumentType;
                var index = 0;

                do
                {
                    index = _topologicallyOrderedTypes.IndexOf(documentType);
                    documentType = documentType.BaseType;
                } while (index == -1 && documentType != null);

                return index;
            }
        }
    }

    public abstract class DocumentStorageOperation: IStorageOperation
    {
        public UpdateStyle UpdateStyle { get; }

        protected DocumentStorageOperation(UpdateStyle updateStyle, object document)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            UpdateStyle = updateStyle;
        }

        public Type DocumentType => Document.GetType();

        public object Document { get; }

        public void ConfigureCommand(CommandBuilder builder)
        {
        }

        public void AddParameters(IBatchCommand batch)
        {
        }

        public string TenantOverride { get; set; }

        public bool Persist(UpdateBatch batch, ITenant tenant)
        {
            var upsert = tenant.StorageFor(Document.GetType());
            upsert.RegisterUpdate(TenantOverride, UpdateStyle, batch, Document);

            return true;
        }
    }

    public class UpsertDocument: DocumentStorageOperation
    {
        public UpsertDocument(object document) : base(UpdateStyle.Upsert, document)
        {
        }

        public UpsertDocument(object document, string tenantId) : this(document)
        {
            TenantOverride = tenantId;
        }

        public override string ToString()
        {
            return $"{GetType().Name}: {DocumentType.Name}";
        }
    }

    public class UpdateDocument: DocumentStorageOperation
    {
        public UpdateDocument(object document) : base(UpdateStyle.Update, document)
        {
        }

        public override string ToString()
        {
            return $"{GetType().Name}: {DocumentType.Name}";
        }
    }

    public class InsertDocument: DocumentStorageOperation
    {
        public InsertDocument(object document) : base(UpdateStyle.Insert, document)
        {
        }

        public override string ToString()
        {
            return $"{GetType().Name}: {DocumentType.Name}";
        }
    }
}
