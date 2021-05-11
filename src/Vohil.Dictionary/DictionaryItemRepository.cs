﻿namespace Vohil.Dictionary
{
	using System;
	using Messaging;
	using Sitecore.Framework.Messaging;
	using System.Collections.Generic;
	using System.Linq;
	using Extensions;
	using Sitecore;
	using Sitecore.Abstractions;
	using Sitecore.Data;
	using Sitecore.Data.Items;
	using Sitecore.Data.Managers;
	using Sitecore.Globalization;
	using Sitecore.Publishing;
	using Sitecore.SecurityModel;

	public class DictionaryItemRepository
	{
		private readonly DictionarySettings settings;
		private readonly IMessageBus<DictionaryMessageBus> messageBus;
		private readonly BaseLog logger;
		private static readonly object SyncRoot = new object();

		public DictionaryItemRepository(
			DictionarySettings settings, 
			IMessageBus<DictionaryMessageBus> messageBus,
			BaseLog logger)
		{
			this.settings = settings;
			this.messageBus = messageBus;
			this.logger = logger;
		}

		public virtual void Create(string dictionaryKey, string defaultValue, string language)
		{
			language = language ?? Context.Language?.Name;
			var lang = LanguageManager.GetLanguage(language);

			this.messageBus.SendAsync(new CreateDictionaryItemMessage
			{
				DictionaryKey = dictionaryKey,
				DefaultValue = defaultValue,
				Database = this.settings.ItemCreationDatabase,
				Language = lang.Name,
				DictionaryDomainId = this.GetDomainItem(lang).ID.Guid
			});
		}

		public virtual Item Get(string key, string language, Guid? domainId = null)
		{
			var lang = LanguageManager.GetLanguage(language);
			var itemPath = this.GetPhraseItemPath(key, lang, domainId);
			return Context.Database.GetItem(itemPath, lang);
		}

		public virtual string GetPhraseItemPath(string key, Language language, Guid? domainId = null)
		{
			var splittedValue = key.Split(new[] { ".", "/" }, StringSplitOptions.RemoveEmptyEntries);
			var relativeItemPath = string.Join("/", splittedValue.Select(x => ItemUtil.ProposeValidItemName(x).ToPascalCase()));
			return this.GetDomainItem(language, domainId).Paths.FullPath + "/" + relativeItemPath;
		}

		public virtual Item GetDomainItem(Language language, Guid? domainId = null)
		{
			if (!domainId.HasValue)
			{
				var siteDomainId = Context.Site?.DictionaryDomain;
				if (!string.IsNullOrWhiteSpace(siteDomainId) && ID.IsID(siteDomainId))
				{
					domainId = ID.Parse(siteDomainId).Guid;
				}
			}

			var dictionaryDomain = domainId.HasValue 
				? Context.Database.GetItem(new ID(domainId.Value), language)
				: null;

			return dictionaryDomain ?? Context.Database.GetItem("/sitecore/system/Dictionary/", language);
		}

		public virtual void PublishItem(Item item, Guid domainId)
		{
			var toPublishList = new List<Item>();
			var dictionaryRoot = this.GetDomainItem(item.Language, domainId);

			if (dictionaryRoot != null)
			{
				var publishingItem = item;

				toPublishList.Add(publishingItem);

				while (publishingItem.ID != dictionaryRoot.ID)
				{
					toPublishList.Add(publishingItem.Parent);
					publishingItem = publishingItem.Parent;
				}

				toPublishList.Reverse();

				foreach (var toPublish in toPublishList)
				{
					PublishManager.PublishItem(
						toPublish, 
						PublishingTargets(item.Database), 
						new[] { toPublish.Language }, 
						false, 
						false);

					this.logger.Debug(
						$"[Vohil.Dictionary]: Dictionary item is being published... Language: '{toPublish.Language.Name}', " +
						$"dictionary item path: '{toPublish.Paths.FullPath}'",
						this);
				}
			}
			else
			{
				PublishManager.PublishItem(
					item, 
					PublishingTargets(item.Database), 
					new[] { item.Language },
					false, false);

				this.logger.Debug(
					$"[Vohil.Dictionary]: Dictionary item is being published... Language: '{item.Language.Name}', " +
					$"dictionary item path: '{item.Paths.FullPath}'",
					this);
			}
		}

		private static Database[] PublishingTargets(Database database)
		{
			var targets = new List<Database>();

			var publishingTargetsRoot = database.GetItem("{D9E44555-02A6-407A-B4FC-96B9026CAADD}");
			foreach (Item target in publishingTargetsRoot.Children)
			{
				var value = target[ID.Parse("{39ECFD90-55D2-49D8-B513-99D15573DE41}")];
				if (!string.IsNullOrWhiteSpace(value))
				{
					var db = Database.GetDatabase(value);
					if (db != null)
					{
						targets.Add(db);
					}
				}
			}

			return targets.ToArray();
		}

		public void CreateItem(string key, string defaultValue, Guid domainId, Language language, Database database)
		{
			if (this.Get(key, language.Name, domainId) == null)
			{
				lock (SyncRoot)
				{
					if (this.Get(key, language.Name, domainId) == null)
					{
						var phrasePath = this.GetPhraseItemPath(key, language, domainId);

						var folderTemplate = database.GetTemplate(new ID(this.settings.DictionaryFolderTemplateId));
						var phraseTemplate = database.GetTemplate(new ID(this.settings.DictionaryPhraseTemplateId));

						using (new SecurityDisabler())
						{
							var item = database.CreateItemPath(phrasePath, folderTemplate, phraseTemplate);

							this.logger.Info(
								$"[Vohil.Dictionary]: Dictionary phrase item with key '{key}' has been created. " +
								$"Language: '{item.Language.Name}', item path: '{item.Paths.FullPath}'",
								this);
						}
					}
				}
			}

			var phraseItem = this.Get(key, language.Name, domainId);

			if (!phraseItem.Versions.GetVersions().Any())
			{
				phraseItem.Versions.AddVersion();
				phraseItem = this.Get(key, language.Name, domainId);

				this.logger.Info(
						$"[Vohil.Dictionary]: Added '{phraseItem.Language.Name}' language version " +
						$"to phrase item '{phraseItem.Paths.FullPath}'",
					this);
			}

			var keyIsEmpty = string.IsNullOrWhiteSpace(phraseItem[this.settings.DictionaryKeyFieldName]);
			var phraseIsEmpty = string.IsNullOrWhiteSpace(phraseItem[this.settings.DictionaryPhraseFieldName]);

			if (keyIsEmpty || phraseIsEmpty)
			{
				using (new SecurityDisabler())
				{
					using (new EditContext(phraseItem))
					{
						phraseItem[this.settings.DictionaryKeyFieldName] = key;
						phraseItem[this.settings.DictionaryPhraseFieldName] = defaultValue;
					}

					this.logger.Debug(
						$"[Vohil.Dictionary]: Updated '{phraseItem.Language.Name}' language version of phrase item with key '{key}' " +
						$"and default value '{defaultValue}'. item path: '{phraseItem.Paths.FullPath}'.",
						this);

					this.PublishItem(phraseItem, domainId);
				}
			}
		}
	}
}