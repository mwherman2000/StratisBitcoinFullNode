﻿using NBitcoin.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using System.Collections.Concurrent;
using NBitcoin.Protocol.Behaviors;
using System.Threading;

namespace Stratis.Bitcoin.BlockPulling
{
	public class NodesBlockPuller : LookaheadBlockPuller
	{
		public class NodesBlockPullerBehavior : NodeBehavior
		{
			private readonly NodesBlockPuller _Puller;
			
			public NodesBlockPullerBehavior(NodesBlockPuller puller)
			{
				_Puller = puller;
			}
			public override object Clone()
			{
				return new NodesBlockPullerBehavior(_Puller);
			}

			public int StallingScore
			{
				get; set;
			} = 1;



			private ConcurrentDictionary<uint256, uint256> _PendingDownloads = new ConcurrentDictionary<uint256, uint256>();
			public ICollection<uint256> PendingDownloads
			{
				get
				{
					return _PendingDownloads.Values;
				}
			}

			private void Node_MessageReceived(Node node, IncomingMessage message)
			{
				message.Message.IfPayloadIs<BlockPayload>((block) =>
				{
					block.Object.Header.CacheHashes();
					StallingScore = Math.Max(1, StallingScore - 1);
					uint256 unused;
					if(!_PendingDownloads.TryRemove(block.Object.Header.GetHash(), out unused))
					{
						//Unsollicited
						return;
					}
					NodesBlockPullerBehavior unused2;
					if(_Puller._Map.TryRemove(block.Object.Header.GetHash(), out unused2))
					{
						foreach(var tx in block.Object.Transactions)
							tx.CacheHashes();
						_Puller.PushBlock((int)message.Length, block.Object);
						AssignPendingVector();
					}
				});
			}

			internal void AssignPendingVector()
			{
				if(AttachedNode != null && AttachedNode.State != NodeState.HandShaked)
					return;
				uint256 block;
				if(_Puller._PendingInventoryVectors.TryTake(out block))
				{
					StartDownload(block);
				}
			}

			internal void StartDownload(uint256 block)
			{
				if(_Puller._Map.TryAdd(block, this))
				{
					_PendingDownloads.TryAdd(block, block);
					AttachedNode.SendMessageAsync(new GetDataPayload(new InventoryVector(InventoryType.MSG_BLOCK, block)));
				}
			}

			//Caller should add to the puller map
			internal void StartDownload(GetDataPayload getDataPayload)
			{
				foreach(var inv in getDataPayload.Inventory)
				{
					_PendingDownloads.TryAdd(inv.Hash, inv.Hash);
				}
				AttachedNode.SendMessageAsync(getDataPayload);
			}

			protected override void AttachCore()
			{
				AttachedNode.MessageReceived += Node_MessageReceived;
				AssignPendingVector();
			}

			protected override void DetachCore()
			{
				AttachedNode.MessageReceived += Node_MessageReceived;
				foreach(var download in _Puller._Map.ToArray())
				{
					if(download.Value == this)
					{
						Release(download.Key);
					}
				}
			}

			internal void Release(uint256 blockHash)
			{
				NodesBlockPullerBehavior unused;
				uint256 unused2;
				if(_Puller._Map.TryRemove(blockHash, out unused))
				{
					_PendingDownloads.TryRemove(blockHash, out unused2);
					_Puller._PendingInventoryVectors.Add(blockHash);
				}
			}
		}

		NodesCollection _Nodes;
		ConcurrentChain _Chain;
		public NodesBlockPuller(ConcurrentChain chain, NodesCollection nodes)
		{
			_Chain = chain;
			_Nodes = nodes;
		}

		ConcurrentDictionary<uint256, NodesBlockPullerBehavior> _Map = new ConcurrentDictionary<uint256, NodesBlockPullerBehavior>();
		ConcurrentBag<uint256> _PendingInventoryVectors = new ConcurrentBag<uint256>();

		protected override void AskBlocks(ChainedBlock[] downloadRequests)
		{
			var busyNodes = new HashSet<NodesBlockPullerBehavior>(_Map.Select(m => m.Value).Distinct());
			var idleNodes = _Nodes.Select(n => n.Behaviors.Find<NodesBlockPullerBehavior>())
								  .Where(n => !busyNodes.Contains(n)).ToArray();
			if(idleNodes.Length == 0)
				idleNodes = busyNodes.ToArray();

			var vectors = downloadRequests.Select(r => new InventoryVector(InventoryType.MSG_BLOCK, r.HashBlock)).ToArray();
			DistributeDownload(vectors, idleNodes);
		}

		protected override void OnStalling(ChainedBlock chainedBlock, int inARow)
		{
			NodesBlockPullerBehavior behavior = null;
			if(_Map.TryGetValue(chainedBlock.HashBlock, out behavior))
			{
				behavior.StallingScore = Math.Min(MaxStallingScore, behavior.StallingScore + inARow);
				if(behavior.StallingScore == MaxStallingScore)
				{
					behavior.Release(chainedBlock.HashBlock);
				}
			}
			else
			{
				foreach(var node in _Nodes.Select(n => n.Behaviors.Find<NodesBlockPullerBehavior>()))
					node.AssignPendingVector();
			}
		}

		private void DistributeDownload(InventoryVector[] vectors, NodesBlockPullerBehavior[] idleNodes)
		{
			if(idleNodes.Length == 0)
			{
				foreach(var v in vectors)
					_PendingInventoryVectors.Add(v.Hash);
				return;
			}
			var scores = idleNodes.Select(n => n.StallingScore).ToArray();
			var totalScore = scores.Sum();
			GetDataPayload[] getDatas = idleNodes.Select(n => new GetDataPayload()).ToArray();
			//TODO: Be careful to not ask block to a node that do not have it (we can check the ChainBehavior.PendingTip to know where the node is standing)
			foreach(var inv in vectors)
			{
				var index = GetNodeIndex(scores, totalScore);
				var node = idleNodes[index];
				var getData = getDatas[index];
				if(_Map.TryAdd(inv.Hash, node))
					getData.Inventory.Add(inv);
			}
			for(int i = 0; i < idleNodes.Length; i++)
			{
				idleNodes[i].StartDownload(getDatas[i]);
			}
		}

		const int MaxStallingScore = 150;
		Random _Rand = new Random();
		//Chose random index proportional to the score
		private int GetNodeIndex(int[] scores, int totalScore)
		{
			var v = _Rand.Next(totalScore);
			var current = 0;
			int i = 0;
			foreach(var score in scores)
			{
				current += MaxStallingScore - score;
				if(v < current)
					return i;
				i++;
			}
			return scores.Length - 1;
		}

		protected override ConcurrentChain ReloadChainCore()
		{
			return _Chain;
		}
	}
}
