using VestPocket;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using System.Threading;

namespace VestPocket.Test
{

    public class VestPocketStoreTests : IClassFixture<VestPocketStoreFixture>
    {

        private VestPocketStore<Entity> testStore;
        private VestPocketStoreFixture fixture;

        public VestPocketStoreTests(VestPocketStoreFixture fixture)
        {
            this.fixture = fixture;
            if (testStore != null && !testStore.IsDisposed)
            {
                testStore.Close(CancellationToken.None).Wait();
            }
            this.testStore = fixture.Get(VestPocketOptions.Default);
        }

        [Fact]
        public async Task CanCRUD()
        {
            var key = "crud";
            var entity = new TestDocument (key, 0, false, "crud body");
            var entitySaved = await testStore.Save(entity);
            var entitySavedExpected = entity with {Version = 1};
            
            Assert.Equal(entitySavedExpected, entitySaved);

            var entityRetreived = testStore.Get<TestDocument>(key);
            Assert.Equal(entitySavedExpected, entityRetreived);

            var entityToUpdate = entityRetreived with {Body = "crud body updated"}; 
            var expectedUpdatedEntity = entityToUpdate with {Version = 2};
            var entityUpdated = await testStore.Save(entityToUpdate);

            Assert.Equal(expectedUpdatedEntity, entityUpdated);

            var toDelete = entityUpdated with { Deleted = true };
            await testStore.Save(toDelete);
            var deletedDocument = testStore.Get<TestDocument>(key);
            Assert.Null(deletedDocument);
        }

        [Fact]
        public async Task TracksDeadSpace()
        {
            string key = "key";
            var entity1 = new TestDocument(key, 0, false, "some contents1");
            await testStore.Save(entity1);
            var entity2 = entity1 with { Body = "some contents2", Version = 1};
            await testStore.Save(entity2);
            var deadSpace = testStore.DeadEntityCount;
            var deadSpaceGreaterThanZero = deadSpace > 0;
            Assert.True(deadSpaceGreaterThanZero);
        }

        [Fact] 
        public async Task CanMaintainDeadSpace()
        {
            string key = "key";
            var entity1 = new TestDocument(key, 0, false, "some contents1");
            await testStore.Save(entity1);
            var entity2 = entity1 with { Body = "some contents2", Version = 1 };
            await testStore.Save(entity2);
            var deadSpace = testStore.DeadEntityCount;

            var deadSpaceBeforeMaintenance = testStore.DeadEntityCount;
            await testStore.ForceMaintenance();
            var deadSpaceAfterMaintenance = testStore.DeadEntityCount;
            var deadSpaceAfterMaintenanceIsLess = deadSpaceBeforeMaintenance > deadSpaceAfterMaintenance;
            Assert.True(deadSpaceAfterMaintenanceIsLess);
        }

        [Fact]
        public async Task PrefixSearch_PartialMatch()
        {
            string key = "prefix";
            string prefix = "pre";
            string body = "body";
            var expectedDocument = await testStore.Save(new TestDocument(key, 0, false, body));
            var prefixResults = testStore.GetByPrefix<TestDocument>(prefix, false);
            var firstMatchByPrefix = prefixResults.FirstOrDefault();
            Assert.Equal(expectedDocument, firstMatchByPrefix);
        }

        [Fact]
        public async Task PrefixSearch_ExactMatch()
        {
            string key = "prefix";
            string prefix = "prefix";
            string body = "body";

            var expectedDocument = await testStore.Save(new TestDocument(key, 0, false, body));
            var prefixResults = testStore.GetByPrefix<TestDocument>(prefix, false);
            var firstMatchByPrefix = prefixResults.FirstOrDefault();
            Assert.Equal(expectedDocument, firstMatchByPrefix);
        }

        [Fact]
        public async Task PrefixSearch_CanSortResults()
        {
            string aKey = "AKey";
            string bKey = "BKey";
            string cKey = "CKey";

            string body = "body";

            var cDoc = await testStore.Save(new TestDocument(cKey, 0, false, body));
            var bDoc = await testStore.Save(new TestDocument(bKey, 0, false, body));
            var aDoc = await testStore.Save(new TestDocument(aKey, 0, false, body));

            var prefixResults = testStore.GetByPrefix<TestDocument>("", true).ToArray();

            Assert.Equal(3, prefixResults.Length);
            Assert.Equal(aDoc, prefixResults[0]);
            Assert.Equal(bDoc, prefixResults[1]);
            Assert.Equal(cDoc, prefixResults[2]);
        }

        [Fact]
        public async Task KeysAreCaseSensitive()
        {
            var key = "crud";
            var notKey = "CrUd";

            await testStore.Save(new TestDocument(key, 0, false, "doc1"));
            await testStore.Save(new TestDocument(notKey, 0, false, "doc2"));

            var doc1 = testStore.Get<TestDocument>(key);
            var doc2 = testStore.Get<TestDocument>(notKey);

            Assert.NotEqual(doc1.Body, doc2.Body);
        }

        [Fact]
        public async Task Set_FailsWithOldVersionDocument()
        {
            var key = "crud";
            var version1 = await testStore.Save(new TestDocument(key, 0, false, "doc1"));
            var version0 = version1 with { Version = 0 };
            await Assert.ThrowsAsync<ConcurrencyException>(async () => await testStore.Save(version0));
        }

        [Fact]
        public async Task Delete_FailsWithOldVersionDocument()
        {
            var key = "crud";
            var version1 = await testStore.Save(new TestDocument(key, 0, false, "doc1"));
            var version0 = version1 with { Deleted = true, Version = 0 };
            await Assert.ThrowsAsync<ConcurrencyException>(async () => await testStore.Save(version0));
        }

        [Fact]
        public async Task Save_CanProcessMultipleEntitiesAsTransaction()
        {

            var changes = new Entity[]
            {
                new TestDocument("1", 0, false, "body1"),
                new TestDocument("2", 0, false, "body2"),
                new Entity("3", 0, false)
            };

            var updated = await testStore.Save(changes);

            var expected = changes.Length;
            var actual = updated.Length;

            Assert.Equal(expected, actual);
        }

        /// <summary>
        /// Saves a document to the database, then performs a multi set that includes an update to the same document but with an
        /// older version number. Expected result is a failure
        /// </summary>
        /// <returns></returns>
        [Fact]
        public async Task Save_AllChangesRejectedOnAnyOldVersionDocument()
        {

            var testDoc = await testStore.Save(new TestDocument("1", 0, false, "body1"));
            
            var docWithOldVersion = testDoc with { Version= 0 }; 

            Entity[] changes = new Entity[]
            {
                docWithOldVersion,
                new TestDocument("2", 0, false, "body2"),
                new Entity("3", 0, false)
            };

            await Assert.ThrowsAsync<ConcurrencyException>(async() => await testStore.Save(changes));
        }

        [Fact]
        public async Task Save_FailsWhenReadOnly()
        {
            var readonlyStore = fixture.Get(VestPocketOptions.DefaultReadOnly, false);
            var testDocument = new TestDocument("key", 0, false, "body");
            await Assert.ThrowsAsync<Exception>(async () => await readonlyStore.Save(testDocument));
        }


    }
}