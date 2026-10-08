using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelShareablesTests
    {
        private readonly List<Guid> _registered = new List<Guid>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _registered.Count; i++)
                BasisModelShareables.Unregister(_registered[i]);
            _registered.Clear();
        }

        private void Register(Guid id, string title, Action onDestructive)
        {
            _registered.Add(id);
            BasisModelShareables.Register(id, title, "Sharer", onDestructive);
        }

        private static BasisShareableEntry Find(string key)
        {
            foreach (BasisShareableEntry entry in BasisShareableRegistry.GetAll())
            {
                if (entry.Id == key)
                    return entry;
            }
            return null;
        }

        [Test]
        public void ModelsAreListedAsPropsUnderAKeyNoOtherEntryUses()
        {
            Guid id = BasisModelShareTestIds.Make(1);
            int deletes = 0;
            Register(id, "model", () => deletes++);

            string key = BasisModelShareables.KeyFor(id);
            Assert.That(key, Is.Not.EqualTo(id.ToString()), "the image pickup keys its entries by the bare id");
            Assert.That(key, Is.Not.EqualTo(id.ToString("N")));

            BasisShareableEntry entry = Find(key);
            Assert.That(entry, Is.Not.Null);
            Assert.That(entry.Title, Is.EqualTo("model"));
            Assert.That(entry.Kind, Is.EqualTo(BasisShareableKind.Prop));
            Assert.That(entry.SharerName, Is.EqualTo("Sharer"));
            Assert.That(entry.Actions.Count, Is.EqualTo(1));
            Assert.That(entry.Actions[0].Style, Is.EqualTo(BasisShareableActionStyle.Destructive));

            entry.Actions[0].Invoke();
            Assert.That(deletes, Is.EqualTo(1));

            BasisModelShareables.Unregister(id);
            Assert.That(Find(key), Is.Null);
        }

        [Test]
        public void SetTitleRenamesTheEntry()
        {
            Guid id = BasisModelShareTestIds.Make(2);
            Register(id, "Loading", () => { });

            BasisModelShareables.SetTitle(id, "1,234 triangles");

            Assert.That(Find(BasisModelShareables.KeyFor(id)).Title, Is.EqualTo("1,234 triangles"));
        }

        [Test]
        public void NoCallbackMeansNoRemoveButton()
        {
            Guid id = BasisModelShareTestIds.Make(3);
            Register(id, "model", null);

            Assert.That(Find(BasisModelShareables.KeyFor(id)).Actions, Is.Empty);
        }
    }
}
