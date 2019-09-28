using System;
using System.Collections.Generic;

namespace Marten.Schema.Identity
{
    public class GuidIdGeneration: IIdGeneration
    {
        public IEnumerable<Type> KeyTypes { get; } = new[] { typeof(Guid) };

        public IIdGenerator<T> Build<T>()
        {
            return (IIdGenerator<T>)new GuidIdGenerator(Guid.NewGuid);
        }

        public bool RequiresSequences { get; } = false;
    }
}
