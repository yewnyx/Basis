using System;
using System.Collections.Generic;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Library ("Instantiated" tab) entries for models. They are listed as props, which is what the server's
    /// props gate treats them as. Keys carry a model prefix, so a peer reusing an image's or a content
    /// sphere's id cannot overwrite that entry or point its Delete at the wrong item. The Library treats the
    /// key as opaque.
    /// </summary>
    public static class BasisModelShareables
    {
        public const BasisShareableKind Kind = BasisShareableKind.Prop;

        private const string KeyPrefix = "model:";

        public static string KeyFor(Guid id)
        {
            return KeyPrefix + id.ToString("N");
        }

        /// <param name="onDestructive">The Library's remove button; null shows none.</param>
        public static void Register(Guid id, string title, string sharerName, Action onDestructive)
        {
            var actions = new List<BasisShareableAction>(1);
            if (onDestructive != null)
            {
                actions.Add(
                    new BasisShareableAction { Style = BasisShareableActionStyle.Destructive, Invoke = onDestructive }
                );
            }
            BasisShareableRegistry.Register(
                new BasisShareableEntry
                {
                    Id = KeyFor(id),
                    Kind = Kind,
                    Title = title,
                    SharerName = sharerName,
                    Actions = actions,
                }
            );
        }

        public static void Unregister(Guid id)
        {
            BasisShareableRegistry.Unregister(KeyFor(id));
        }

        /// <summary>Replaces the entry's title, e.g. once a loading model knows what it is.</summary>
        public static void SetTitle(Guid id, string title)
        {
            BasisShareableRegistry.SetDetail(KeyFor(id), title);
        }
    }
}
