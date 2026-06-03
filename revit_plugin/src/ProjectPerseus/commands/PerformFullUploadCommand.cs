using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using ProjectPerseus.auth;
using ProjectPerseus.config;
using ProjectPerseus.revit;
using ProjectPerseus.sync;
using ProjectPerseus.web;

using ProjectPerseus.logging;
namespace ProjectPerseus.commands
{
    [Transaction(TransactionMode.Manual)]
    public class PerformFullUploadCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument.Document;

                if (doc == null)
                {
                    Log.Info("PerformFullUploadCommand failed: No active document.");
                    return Result.Failed;
                }

                var revit = new RevitFacade(doc);
                string batchId = Guid.NewGuid().ToString();
                Log.Info($"PerformFullUploadCommand SyncBatch: {batchId}");

                FullSyncRunner.PerformFullSync(revit, batchId);

                try
                {
                    var docGuid = ModelGuidStorage.GetOrCreate(doc);
                    var batchCloseEndpoint = $"{Config.Instance.BaseUrl}/batchclose/";
                    var payload = JsonConvert.SerializeObject(new { batchId = batchId, documentGuid = docGuid });
                    WebHelper.Post(batchCloseEndpoint, AuthService.GetAuthTokenSafely(), payload);
                    Log.Info($"PerformFullUploadCommand SyncBatch closed: {batchId}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"batchClose call failed (non-fatal): {ex.Message}");
                }

                TaskDialog.Show("Perseus", "Full upload complete.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Info($"Manual Full Sync failed: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
