using System.Collections.Generic;
using ScsContentMigrator.Models;

namespace ScsContentMigrator.Core.Interface
{
	public interface IContentMigration
	{
		ContentMigrationOperationStatus Status { get; }
		IDictionary<string, int> Statistics { get; }
		int ItemsInQueueToInstall { get;}
		void CancelMigration();
		void StartContentMigration(PullItemModel args);
		void StartOperationFromPreview();
		IEnumerable<dynamic> GetItemLogEntries(int lineToStartFrom);
		IEnumerable<string> GetAuditLogEntries(int lineToStartFrom);
	}
}
