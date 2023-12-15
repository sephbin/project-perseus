using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ARDB = Autodesk.Revit.DB;


namespace ProjectPerseus
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProjectPerseusApp : IExternalApplication
    {
        private Config config = Config.Instance; 
        
        public Result OnStartup(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronizedWithCentral;
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;
            
            addSettingsToolbarButton(application);

            return Result.Succeeded;
        }

        private static void addSettingsToolbarButton(UIControlledApplication application)
        {
            RibbonPanel ribbonPanel = application.CreateRibbonPanel("Project Perseus");

            string thisAssemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;

            // add a command to open the settings window
            var buttonData = new PushButtonData("ProjectPerseusSettings", "Project Perseus Settings", thisAssemblyPath,
                typeof(EditSettingsCommand).FullName);
            buttonData.ToolTip = "Project Perseus";
            // todo: add icons
            //     buttonData.LargeImage = PngImageSource("ProjectPerseus.Resources.ProjectPerseus.png");
            //     buttonData.ToolTipImage = PngImageSource("ProjectPerseus.Resources.ProjectPerseus.png");

            ribbonPanel.AddItem(buttonData);
        }

        private void OnDocumentOpened(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
        }

        private void OnDocumentSynchronizedWithCentral(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            doOnSync(e);
        }

        private void doOnSync(DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                if(uploadConfigIsValid() == false)
                {
                    Log.Warn("Upload config is not valid - skipping upload.");
                    return;
                }
                
                var elements = new ElementExtractor(e.Document).ExtractElements();
                new ProjectPerseusWeb(config.BaseUrl, config.ApiToken).UploadElements(elements);
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }
        }

        private bool uploadConfigIsValid()
        {
            return !string.IsNullOrEmpty(config.ApiToken) 
                   && config.BaseUrl != null 
                   && Utl.IsValidUrl(config.BaseUrl);
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.ControlledApplication.DocumentSynchronizedWithCentral -= OnDocumentSynchronizedWithCentral;
            application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
            return Result.Succeeded;
        }
    }
}