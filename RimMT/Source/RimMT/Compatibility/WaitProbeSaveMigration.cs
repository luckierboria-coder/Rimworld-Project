using System.Threading;
using Verse;

namespace RimMTWaitProbe
{
    // Save compatibility for the retired standalone WaitProbe. The old save node resolves
    // to this type once, then removes itself so the next save no longer writes the node.
    public sealed class WaitAuthorityGameComponent : GameComponent
    {
        private readonly Game game;
        private int removalQueued;

        public WaitAuthorityGameComponent(Game game)
        {
            this.game = game;
        }

        public override void LoadedGame()
        {
            QueueRemoval();
        }

        public override void StartedNewGame()
        {
            QueueRemoval();
        }

        private void QueueRemoval()
        {
            if (Interlocked.Exchange(ref removalQueued, 1) != 0)
                return;
            LongEventHandler.ExecuteWhenFinished(RemoveFromGame);
        }

        private void RemoveFromGame()
        {
            if (game == null || game.components == null)
                return;

            game.components.Remove(this);
            Log.Message("[RimMT] Removed retired RimMTWaitProbe.WaitAuthorityGameComponent from the loaded game; the next save will no longer contain it.");
        }
    }
}
