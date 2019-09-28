using System;
using System.Data;
using Marten.Services;
using Xunit;

namespace Marten.Testing.Bugs
{
    public class Account
    {
        public Guid Id { get; set; }
        public decimal Amount { get; set; }

        public void Substract(int value)
        {
            Amount = Amount - value;
        }
    }

    public class Bug_616_not_possible_to_use_Serializable_transactions: DocumentSessionFixture<NulloIdentityMap>
    {
        [Fact]
        public void conccurent_write_should_throw_an_exception()
        {
            var accountA = new Account { Id = Guid.NewGuid(), Amount = 100 };
            theSession.Store(accountA);
            theSession.SaveChanges();

            using (var session1 = theStore.DirtyTrackedSession(IsolationLevel.Serializable))
            using (var session2 = theStore.DirtyTrackedSession(IsolationLevel.Serializable))
            {
                var session1AcountA = session1.Load<Account>(accountA.Id);
                session1AcountA.Substract(500);

                var session2AcountA = session2.Load<Account>(accountA.Id);
                session2AcountA.Substract(350);

                session1.SaveChanges();

                Assert.Throws<ConcurrentUpdateException>(() =>
                {
                    session2.SaveChanges();
                });
            }
        }
    }
}
