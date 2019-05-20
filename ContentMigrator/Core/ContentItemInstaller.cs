﻿using Rainbow.Diff;
using Rainbow.Model;
using Rainbow.Storage;
using Rainbow.Storage.Sc.Deserialization;
using ScsContentMigrator.CMRainbow;
using ScsContentMigrator.Core.Interface;
using ScsContentMigrator.Models;
using ScsContentMigrator.Services.Interface;
using Sitecore.Data;
using Sitecore.Data.Events;
using Sitecore.Data.Items;
using Sitecore.Data.Managers;
using Sitecore.Data.Serialization.Exceptions;
using Sitecore.Diagnostics;
using Sitecore.SecurityModel;
using SitecoreSidekick;
using SitecoreSidekick.ContentTree;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ScsContentMigrator.CMRainbow.Interface;
using ScsContentMigrator.DataBlaster;
using ScsContentMigrator.DataBlaster.Sitecore.DataBlaster.Load;
using Sitecore.Data.Engines;
using Sitecore.Data.Proxies;
using SitecoreSidekick.Services.Interface;

namespace ScsContentMigrator.Core
{
	public class ContentItemInstaller : IContentItemInstaller
	{
		private readonly IDefaultLogger _logger;
		private readonly IDataStore _scDatastore;
		private readonly IItemComparer _comparer;
		private readonly ISitecoreDataAccessService _sitecore;
		private readonly IJsonSerializationService _jsonSerializationService;
		private readonly IChecksumManager _checksumManager;
		private readonly BlockingCollection<IItemData> _itemsToCreate = new BlockingCollection<IItemData>();
		internal ConcurrentHashSet<Guid> AllowedItems = new ConcurrentHashSet<Guid>();
		internal ConcurrentHashSet<Guid> Errors = new ConcurrentHashSet<Guid>();
		internal ConcurrentHashSet<Guid> CurrentlyProcessing = new ConcurrentHashSet<Guid>();
		internal Dictionary<string, int> OperationStatistics = new Dictionary<string, int>();
		internal int WaitForParentDelay = 50;
		private readonly object _locker = new object();
		public ContentMigrationOperationStatus Status { get; } = new ContentMigrationOperationStatus();
		public IDictionary<string, int> Statistics { get => this.OperationStatistics; }

		public ContentItemInstaller()
		{
			_logger = Bootstrap.Container.Resolve<IDefaultLogger>();
			_scDatastore = Bootstrap.Container.Resolve<IDataStore>(_logger);
			_sitecore = Bootstrap.Container.Resolve<ISitecoreDataAccessService>();
			_jsonSerializationService = Bootstrap.Container.Resolve<IJsonSerializationService>();
			_comparer = Bootstrap.Container.Resolve<IItemComparer>();
			_checksumManager = Bootstrap.Container.Resolve<IChecksumManager>();
		}

		public IEnumerable<dynamic> GetItemLogEntries(int lineToStartFrom)
		{
			for (int i = lineToStartFrom; i < _logger.Lines.Count; i++)
				yield return _logger.Lines[i];
		}

		public IEnumerable<string> GetAuditLogEntries(int lineToStartFrom)
		{
			for (int i = lineToStartFrom; i < _logger.LoggerOutput.Count; i++)
				yield return _logger.LoggerOutput[i];
		}

		public void CleanUnwantedLocalItems()
		{
			foreach (Guid id in AllowedItems)
			{
				try
				{
					var data = _sitecore.GetItemData(id);
					_logger.BeginEvent(data, LogStatus.Recycle, _sitecore.GetIconSrc(data), false);
					this.UpdateStatistics(LogStatus.Recycle);
					string status = $"{DateTime.Now:h:mm:ss tt} [RECYCLED] Recycled old item {data?.Name} - {id}";
					_logger.LoggerOutput.Add(status);
					if (data != null)
					{
						_sitecore.RecycleItem(id);
					}

				}
				catch (Exception e)
				{
					_logger.BeginEvent(new ErrorItemData() { Name = id.ToString("B"), Path = e.ToString() }, LogStatus.Error, "", false);
					this.UpdateStatistics(LogStatus.Error);
				}
			}

		}

		public void SetupTrackerForUnwantedLocalItems(IEnumerable<Guid> rootIds, IEnumerable<Guid> idsToExclude, IEnumerable<Guid> idsAndChildrenToExclude)
		{
			AllowedItems = new ConcurrentHashSet<Guid>(_sitecore.GetSubtreeOfGuids(rootIds, idsToExclude, idsAndChildrenToExclude));
		}

		public bool Completed { get; private set; }

		private int ItemsInstalled { get; set; } = 0;

		public void StartInstallingItems(PullItemModel args, BlockingCollection<IItemData> itemsToInstall, int threads, CancellationToken cancellationToken)
		{
			Status.StartedTime = DateTime.Now;
			Status.RootNodes = args.Ids.Select(x => new ContentTreeNode(x));
			Status.IsPreview = args.Preview;
			Status.Server = args.Server;
			Task.Run(() =>
			{
				try
				{
					List<Task> running = new List<Task>();
					for (int i = 0; i < threads; i++)
					{
						running.Add(Task.Run(() => { ItemInstaller(args, itemsToInstall, cancellationToken); }, cancellationToken));
					}

					Task.Run(() => { ItemCreator(args, cancellationToken); }, cancellationToken);
					foreach (var t in running)
					{
						t.Wait(cancellationToken);
					}

					_itemsToCreate.CompleteAdding();
				}
				catch (OperationCanceledException)
				{
					Status.Cancelled = true;
				}
				finally
				{
					Finalize(ItemsInstalled, args);
				}
			}, cancellationToken);
		}
		private IEnumerable<BulkLoadItem> GetAllItemsToCreate(BulkLoadContext context, CancellationToken cancellationToken)
		{
			ItemMapper mapper = new ItemMapper();
			while (!Completed)
			{
				if (_itemsToCreate.TryTake(out var remoteData, int.MaxValue, cancellationToken))
				{
					yield return mapper.ToBulkLoadItem(remoteData, context, BulkLoadAction.Update);
				}
				else
				{
					break;
				}
			}
		}
		private void ItemCreator(PullItemModel args, CancellationToken cancellationToken)
		{
			var bulkLoader = new BulkLoader();
			try
			{

				var context = bulkLoader.NewBulkLoadContext("master");
				bulkLoader.LoadItems(context, GetAllItemsToCreate(context, cancellationToken));
				_checksumManager.RegenerateChecksum();
			}
			catch (OperationCanceledException e)
			{
				Log.Warn("Content migration operation was cancelled", e, this);
				Status.Cancelled = true;
			}
			catch (Exception e)
			{
				Log.Error("Catastrophic error when creating items", e, this);
			}
		}


		private void ItemInstaller(PullItemModel args, BlockingCollection<IItemData> itemsToInstall, CancellationToken cancellationToken)
		{
			Thread.CurrentThread.Priority = ThreadPriority.Lowest;
			BulkUpdateContext bu = null;
			EventDisabler ed = null;
			try
			{
				if (args.BulkUpdate)
				{
					bu = new BulkUpdateContext();
				}
				if (args.EventDisabler)
				{
					ed = new EventDisabler();
				}
				using (new SecurityDisabler())
				using (new SyncOperationContext())
				{
					while (!Completed)
					{
						if (!itemsToInstall.TryTake(out var remoteData, int.MaxValue, cancellationToken))
						{
							break;
						}
						if (!args.UseItemBlaster)
						{
							CurrentlyProcessing.Add(remoteData.Id);
						}

						IItemData localData = _sitecore.GetItemData(remoteData.Id);

						ProcessItem(args, localData, remoteData);

						lock (_locker)
						{
							ItemsInstalled++;
							if (!args.UseItemBlaster)
							{
								CurrentlyProcessing.Remove(remoteData.Id);
							}
						}
					}
				}
			}
			catch (OperationCanceledException e)
			{
				Log.Warn("Content migration operation was cancelled", e, this);
				Status.Cancelled = true;
				lock (_locker)
				{
					if (!Completed)
					{
						Finalize(ItemsInstalled, args);
					}
				}
			}
			catch (Exception e)
			{
				Log.Error("Catastrophic error when installing items", e, this);
			}
			finally
			{
				if (args.BulkUpdate)
				{
					bu?.Dispose();
				}
				if (args.EventDisabler)
				{
					ed?.Dispose();
				}
			}
		}

		private void Finalize(int items, PullItemModel args)
		{
			if (args.RemoveLocalNotInRemote)
			{
				CleanUnwantedLocalItems();
			}

			Completed = true;
			Status.FinishedTime = DateTime.Now;
			Status.Completed = true;
			_logger.Lines.Add(new
			{
				Items = items,
				Time = Status.FinishedTime.Subtract(Status.StartedTime).TotalSeconds,
				Date = Status.FinishedTime.ToString("F"),
				Status.Cancelled
			});
			_logger.LoggerOutput.Add(_jsonSerializationService.SerializeObject(_logger.Lines.Last()));
		}

		internal void ProcessItem(PullItemModel args, IItemData localData, IItemData remoteData)
		{
			AllowedItems.Remove(remoteData.Id);
			if (args.Preview)
			{
				if (localData != null)
				{
					var results = _comparer.Compare(remoteData, localData);
					if (results.AreEqual)
					{
						_logger.BeginEvent(remoteData, LogStatus.Skipped, GetSrc(_sitecore.GetIconSrc(localData)), false);
						this.UpdateStatistics(LogStatus.Skipped);
					}
					else if (results.IsMoved)
					{
						_logger.BeginEvent(remoteData, LogStatus.Moved, GetSrc(_sitecore.GetIconSrc(localData)), false);
						this.UpdateStatistics(LogStatus.Moved);
					}
					else if (results.IsRenamed)
					{
						_logger.BeginEvent(remoteData, LogStatus.Renamed, GetSrc(_sitecore.GetIconSrc(localData)), false);
						this.UpdateStatistics(LogStatus.Renamed);
					}
					else if (results.IsTemplateChanged)
					{
						_logger.BeginEvent(remoteData, LogStatus.TemplateChange, GetSrc(_sitecore.GetIconSrc(localData)), false);
						this.UpdateStatistics(LogStatus.TemplateChange);
					}
					else if (args.Overwrite)
					{
						_logger.BeginEvent(remoteData, LogStatus.Changed, GetSrc(_sitecore.GetIconSrc(localData)), false);
						this.UpdateStatistics(LogStatus.Changed);
					}
					else
					{
						_logger.BeginEvent(remoteData, LogStatus.Skipped, GetSrc(_sitecore.GetIconSrc(localData)), false);
						this.UpdateStatistics(LogStatus.Skipped);
					}
				}
				else
				{
					_logger.BeginEvent(remoteData, LogStatus.Created, "", false);
					this.UpdateStatistics(LogStatus.Created);
				}
			}
			else
			{
				bool skip = false;
				if (!args.Overwrite && localData != null)
				{
					_logger.BeginEvent(remoteData, LogStatus.Skipped, GetSrc(_sitecore.GetIconSrc(localData)), false);
					this.UpdateStatistics(LogStatus.Skipped);
					skip = true;
				}
				if (!skip && localData != null)
				{
					var results = _comparer.Compare(remoteData, localData);
					if (results.AreEqual)
					{
						_logger.BeginEvent(remoteData, LogStatus.Skipped, GetSrc(_sitecore.GetIconSrc(localData)), false);
						this.UpdateStatistics(LogStatus.Skipped);
						skip = true;
					}
				}
				else if (!skip && !args.UseItemBlaster)
				{
					while (CurrentlyProcessing.Contains(remoteData.ParentId))
					{
						if (Errors.Contains(remoteData.ParentId))
						{
							Errors.Add(remoteData.Id);
							skip = true;
							break;
						}

						Task.Delay(WaitForParentDelay).Wait();
					}
				}
				if (!skip)
				{
					try
					{
						if (localData != null || !args.UseItemBlaster)
						{
							_logger.BeginEvent(remoteData, LogStatus.Changed, GetSrc(_sitecore.GetIconSrc(localData)), true);
							this.UpdateStatistics(LogStatus.Changed);
							_scDatastore.Save(remoteData);
						}
						else if (args.UseItemBlaster)
						{
							string icon = remoteData.SharedFields.FirstOrDefault(x => x.NameHint == "__Icon")?.Value;
							if (string.IsNullOrWhiteSpace(icon))
							{
								icon = _sitecore.GetIcon(remoteData.TemplateId);
							}
							_logger.BeginEvent(remoteData, LogStatus.Created, $"/scs/platform/scsicon.scsvc?icon={icon}", false);
							_logger.AddToLog($"{DateTime.Now:h:mm:ss tt} [Created] Staging creation of item using Data Blaster {remoteData.Name} - {remoteData.Id}");
							this.UpdateStatistics(LogStatus.Created);
							_itemsToCreate.Add(remoteData);
						}
						else
						{
							_scDatastore.Save(remoteData);
						}

					}
					catch (TemplateMissingFieldException tm)
					{
						_logger.BeginEvent(new ErrorItemData() { Name = remoteData.Name, Path = tm.ToString() }, LogStatus.Warning, "", false);
						this.UpdateStatistics(LogStatus.Warning);
					}
					catch (ParentItemNotFoundException)
					{
						_logger.BeginEvent(remoteData, LogStatus.SkippedParentError, "", false);	
						this.UpdateStatistics(LogStatus.SkippedParentError);
						Errors.Add(remoteData.Id);
					}
					catch (Exception e)
					{
						Errors.Add(remoteData.Id);
						_logger.BeginEvent(new ErrorItemData() { Name = remoteData?.Name ?? "Unknown item", Path = e.ToString() }, LogStatus.Error, "", false);
						this.UpdateStatistics(LogStatus.Error);
					}
					if (localData != null)
					{
						if (_logger.HasLinesSupportEvents(localData.Id.ToString()))
						{
							_logger.CompleteEvent(localData.Id.ToString());
						}
						else
						{
							_logger.BeginEvent(localData, LogStatus.Skipped, _logger.GetSrc(GetSrc(_sitecore.GetIconSrc(localData))), false);
							this.UpdateStatistics(LogStatus.Skipped);
						}
					}
				}
			}
		}

		private void UpdateStatistics(string key)
		{
			lock (_locker)
			{
				if (this.OperationStatistics.ContainsKey(key))
				{
					this.OperationStatistics[key] = this.OperationStatistics[key] + 1;
				}
				else
				{
					this.OperationStatistics.Add(key, 1);
				}
			}
		}

		private string GetSrc(string imgTag)
		{
			if (string.IsNullOrWhiteSpace(imgTag)) return imgTag;
			int i1 = imgTag.IndexOf("src=\"", StringComparison.Ordinal) + 5;
			int i2 = imgTag.IndexOf("\"", i1, StringComparison.Ordinal);
			return imgTag.Substring(i1, i2 - i1);
		}
	}
}
