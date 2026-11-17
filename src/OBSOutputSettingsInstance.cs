using libomtnet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace omtplugin
{
    internal class OBSOutputSettingsInstance : OBSSourceInstance
    {

        private OBSOutputSettings parent;

        public OBSOutputSettingsInstance(OBSOutputSettings parent, nint source, nint settings) : base(source, settings)
        {
            this.parent = parent;
            UpdateSettings(settings);
        }

        protected override void DisposeInternal()
        {
            base.DisposeInternal();
        }

        public override void UpdateSettings(IntPtr settings)
        {
            try
            {
                if (settings != IntPtr.Zero)
                {
                    OMTLogging.Write("UpdateSettings", "OMTOutput");
                    parent.SaveSettings(settings);
                    OBSOutput.UpdateMainOutput();
                    OBSPreviewOutput.UpdatePreviewOutput();
                }
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTOutputSettings.UpdateSettings");
            }
            base.UpdateSettings(settings);
        }

        public override nint GetProperties()
        {
            try
            {
                IntPtr properties = OBS.obs_properties_create();
                if (properties != IntPtr.Zero)
                {
                    OBS.obs_properties_set_flags(properties, OBS.OBS_PROPERTIES_DEFER_UPDATE);
                    IntPtr enabledProperty = OBS.obs_properties_add_bool(properties, "enabledProperty", "Output Enabled");
                    IntPtr nameProperty = OBS.obs_properties_add_text(properties, "nameProperty", "Output Name", 0);
                    IntPtr previewEnabledProperty = OBS.obs_properties_add_bool(properties, "previewEnabledProperty", "Preview Output Enabled");
                    IntPtr previewNameProperty = OBS.obs_properties_add_text(properties, "previewNameProperty", "Preview Output Name", 0);
                    IntPtr sizeProperty = OBS.obs_properties_add_list(properties, "outputHeightProperty", "Output Size",
                        OBS.obs_combo_type.OBS_COMBO_TYPE_LIST, OBS.obs_combo_format.OBS_COMBO_FORMAT_INT);
                    OBS.obs_property_list_add_int(sizeProperty, "Same as canvas", 0);
                    OBS.obs_property_list_add_int(sizeProperty, "1080 lines (1920x1080 at 16:9)", 1080);
                    OBS.obs_property_list_add_int(sizeProperty, "720 lines (1280x720 at 16:9)", 720);
                    OBS.obs_property_list_add_int(sizeProperty, "540 lines (960x540 at 16:9)", 540);
                    OBS.obs_property_list_add_int(sizeProperty, "360 lines (640x360 at 16:9)", 360);
                    OMTLogging.Write("GetProperties", "OMTOutput");
                }
                return properties;
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTSource.GetProperties");
            }
            return base.GetProperties();
        }
    }
}
