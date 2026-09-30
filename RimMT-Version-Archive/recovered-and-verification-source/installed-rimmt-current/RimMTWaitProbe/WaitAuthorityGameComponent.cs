using System;
using System.Threading;
using Verse;

namespace RimMTWaitProbe;

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
		if (Interlocked.Exchange(ref removalQueued, 1) == 0)
		{
			LongEventHandler.ExecuteWhenFinished((Action)RemoveFromGame);
		}
	}

	private void RemoveFromGame()
	{
		if (game != null && game.components != null)
		{
			game.components.Remove((GameComponent)(object)this);
			Log.Message("[RimMT] Removed retired RimMTWaitProbe.WaitAuthorityGameComponent from the loaded game; the next save will no longer contain it.");
		}
	}
}
