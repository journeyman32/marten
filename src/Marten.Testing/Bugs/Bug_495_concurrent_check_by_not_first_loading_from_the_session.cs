using System;
using Marten.Schema;
using Xunit;

namespace Marten.Testing.Bugs
{
    public class Bug_495_concurrent_check_by_not_first_loading_from_the_session: IntegratedFixture
    {
        [UseOptimisticConcurrency]
        public class Foo
        {
            public string Id { get; set; }
            public string Bar { get; set; }
        }

        [Fact]
        public void cannot_overwrite_when_the_second_object_is_not_loaded_through_the_session_first()
        {
            var id = "foo/" + Guid.NewGuid().ToString("n");

            using (var session = theStore.LightweightSession())
            {
                session.Store(new Foo { Id = id });

                session.SaveChanges();
            }

            Exception<AggregateException>.ShouldBeThrownBy(() =>
            {
                using (var session = theStore.LightweightSession())
                {
                    session.Store(new Foo { Id = id });

                    session.SaveChanges();
                }
            });
        }
    }
}
