using System;
using System.Collections.Generic;
using Rainbow.Model;
using ScsContentMigrator.Args;
using ScsContentMigrator.Data;
using ScsContentMigrator.Models;
using ScsContentMigrator.Security;

namespace ScsContentMigrator.Services.Interface
{
	public interface IRemoteContentService
	{
		IItemData GetRemoteItemData(Guid id, string server);
		ChildrenItemDataModel GetRemoteItemDataWithChildren(Guid id, string server, Dictionary<Guid, string> rev = null, List<Guid> idsAndChildrenToExclude = null);
		IItemData DeserializeYaml(string yaml, Guid id);
		CompareContentTreeNode GetContentTreeNode(RemoteContentTreeArgs args);
		ScsHmacServer HmacServer { get; }
	}
}
