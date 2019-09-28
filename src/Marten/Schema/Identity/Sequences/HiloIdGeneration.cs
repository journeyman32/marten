using System;
using System.Collections.Generic;

namespace Marten.Schema.Identity.Sequences
{
    public class HiloIdGeneration: IIdGeneration
    {
        private readonly HiloSettings _hiloSettings;

        public HiloIdGeneration(Type documentType, HiloSettings hiloSettings)
        {
            _hiloSettings = hiloSettings;
            DocumentType = documentType;
        }

        public Type DocumentType { get; }

        public int MaxLo => _hiloSettings.MaxLo;

        public IEnumerable<Type> KeyTypes { get; } = new[] { typeof(int), typeof(long) };

        public IIdGenerator<T> Build<T>()
        {
            if (typeof(T) == typeof(int))
            {
                return (IIdGenerator<T>)new IntHiloGenerator(DocumentType);
            }

            return (IIdGenerator<T>)new LongHiloGenerator(DocumentType);
        }

        public bool RequiresSequences { get; } = true;
    }
}
