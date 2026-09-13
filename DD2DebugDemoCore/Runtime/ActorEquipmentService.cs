using System;
using Assets.Code.Actor;
using Assets.Code.Item;
using Assets.Code.Library;
using Assets.Code.Utils;
using DD2DebugDemoCore.Diagnostics;

namespace DD2DebugDemoCore.Runtime
{
    public sealed class ActorEquipmentService
    {
        private readonly IDebugDemoLogger _log;

        public ActorEquipmentService(IDebugDemoLogger log = null)
        {
            _log = log ?? NullDebugDemoLogger.Instance;
        }

        public bool TryApplyCombatItem(ActorInstance actor, string itemId, out string error)
        {
            error = string.Empty;
            if (actor == null)
            {
                error = "actor is missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(itemId))
            {
                return true;
            }

            ItemDefinition definition = TryGetItemDefinition(itemId);
            if (definition == null || definition.m_type != ItemType.COMBAT)
            {
                error = "combat item is invalid: " + itemId;
                return false;
            }

            try
            {
                if (HasInventoryItem(actor.GetCombatSkillInventory(), itemId, ItemType.COMBAT))
                {
                    return true;
                }

                for (int i = 0; i < definition.m_maxQty; i++)
                {
                    actor.AddCombatSkillInventoryItem(definition, 1);
                }

                _log.Info("Applied combat item " + itemId + " to " + ActorSkillLoadoutService.DescribeActor(actor) + ".");
                return true;
            }
            catch (Exception ex)
            {
                error = "failed to apply combat item " + itemId + " to " +
                    ActorSkillLoadoutService.DescribeActor(actor) + ": " + ex.Message;
                return false;
            }
        }

        public bool TryApplyTrinket(ActorInstance actor, string itemId, out string error)
        {
            error = string.Empty;
            if (actor == null)
            {
                error = "actor is missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(itemId))
            {
                return true;
            }

            ItemDefinition definition = TryGetItemDefinition(itemId);
            if (definition == null || definition.m_type != ItemType.TRINKET)
            {
                error = "trinket is invalid: " + itemId;
                return false;
            }

            try
            {
                if (HasInventoryItem(actor.GetTrinketInventory(), itemId, ItemType.TRINKET))
                {
                    return true;
                }

                // Check native item conditions before AddItems; invalid trinkets are
                // otherwise moved to the player inventory by TrinketItemInventory.
                if (!actor.GetIsItemConditionsMet(definition))
                {
                    error = "trinket conditions are not met for " + itemId + " on " +
                        ActorSkillLoadoutService.DescribeActor(actor) +
                        " (check prerequisite trinkets)";
                    return false;
                }

                int remainder = actor.GetTrinketInventory().AddItems(definition, 1, false);
                if (remainder != 0 || !HasInventoryItem(actor.GetTrinketInventory(), itemId, ItemType.TRINKET))
                {
                    error = "game rejected trinket " + itemId + " for " +
                        ActorSkillLoadoutService.DescribeActor(actor);
                    return false;
                }

                _log.Info("Applied trinket " + itemId + " to " + ActorSkillLoadoutService.DescribeActor(actor) + ".");
                return true;
            }
            catch (Exception ex)
            {
                error = "failed to apply trinket " + itemId + " to " +
                    ActorSkillLoadoutService.DescribeActor(actor) + ": " + ex.Message;
                return false;
            }
        }

        private static ItemDefinition TryGetItemDefinition(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId) || !SingletonMonoBehaviour<Library<string, ItemDefinition>>.HasInstance(false))
            {
                return null;
            }

            return SingletonMonoBehaviour<Library<string, ItemDefinition>>.Instance.GetLibraryElement(itemId.Trim());
        }

        private static bool HasInventoryItem(ItemInventory inventory, string itemId, ItemType itemType)
        {
            if (inventory == null || string.IsNullOrWhiteSpace(itemId))
            {
                return false;
            }

            try
            {
                foreach (IReadOnlyItemInstance item in inventory.GetItems())
                {
                    ItemDefinition definition = item == null ? null : item.GetItemDefinition();
                    if (definition != null &&
                        definition.m_type == itemType &&
                        string.Equals(definition.m_id, itemId.Trim(), StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }
    }
}
