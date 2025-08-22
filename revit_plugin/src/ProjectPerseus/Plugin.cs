using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ProjectPerseus.models;
using ProjectPerseus.revit;


namespace ProjectPerseus
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class Plugin : IExternalApplication
    {
        private readonly Config _config = Config.Instance; 
        
        public Result OnStartup(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;

            return Result.Succeeded;
        }

        private void OnDocumentSynchronizedWithCentral(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            doOnSync(e);
        }

        private void doOnSync(DocumentSynchronizedWithCentralEventArgs e)
        {
            using (var sentry = new Utl.SentryContext())
            {
                try
                {
                    if (UploadConfigIsValid() == false)
                    {
                        Log.Warn("Upload config is not valid - skipping upload.");
                        return;
                    }

                    // record elapsed time
                    var watch = System.Diagnostics.Stopwatch.StartNew();

                    try
                    {
                        var revit = new RevitFacade(e.Document);

                        if (Config.Instance.FullSyncNextSync)
                        {
                            Log.Info("Full sync requested - uploading all elements...");
                            Config.Instance.FullSyncNextSync = false;

                            PerformFullSync(revit);
                        }
                        else
                        {
                            Log.Info("Incremental sync requested - uploading changed elements...");
                            PerformIncrementalSync(revit);
                        }

                        _config.LastSyncVersionGuid = RevitFacade.GetDocumentVersionGuid(revit.Document);
                    }
                    catch (Exception ex)
                    {
                        Log.Exception(new Exception($"Error performing sync: {ex.Message}", ex));
                    }

                    watch.Stop();
                    Log.Info($"Sync completed in {watch.Elapsed:hh\\:mm\\:ss}");
                    // dump json
                    // Utl.JsonDump(elements, "ElementList");
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }
            }
        }

        private void PerformFullSync(RevitFacade revit)
        {
                var elements = revit.GetAllElements();
                var elementDeltaList = ElementDelta.CreateList(ElementDelta.DeltaAction.Create, elements);
                SubmitElementDeltas(elementDeltaList);
        }

        private void PerformIncrementalSync(RevitFacade revit)
        {
            var elementChangeSet = revit.GetElementChangeSet(_config.LastSyncVersionGuid);
            if (elementChangeSet.ContainsChanges())
            {
                var elementDeltaList = ElementDelta.CreateListFromChangeSet(elementChangeSet);
                SubmitElementDeltas(elementDeltaList);
            }
            else 
            {
                Log.Info("No changes detected - skipping upload.");
            }
        }

        private void SubmitElementDeltas(IList<ElementDelta> elements)
        {
            Log.Info("Submitting element deltas to webservice...");
            new ProjectPerseusWeb(_config.BaseUrl, _config.ApiToken).SubmitElementDeltas(elements);
        }

        private bool UploadConfigIsValid()
        {
            return !string.IsNullOrEmpty(_config.ApiToken) 
                   && _config.BaseUrl != null 
                   && Utl.IsValidUrl(_config.BaseUrl);
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynchronizedWithCentral;
            return Result.Succeeded;
        }
    }
}