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

        private static void AddSizeList(IntPtr properties, string name, string description)
        {
            IntPtr list = OBS.obs_properties_add_list(properties, name, description,
                OBS.obs_combo_type.OBS_COMBO_TYPE_LIST, OBS.obs_combo_format.OBS_COMBO_FORMAT_INT);
            // The lines are scaled at the aspect ratio of OBS's output resolution, so the sizes in
            // brackets hold for 16:9; a 4K or vertical canvas gets the same lines at its own ratio.
            OBS.obs_property_list_add_int(list, "Same as OBS output resolution", 0);
            OBS.obs_property_list_add_int(list, "1080 lines (1920x1080 at 16:9)", 1080);
            OBS.obs_property_list_add_int(list, "720 lines (1280x720 at 16:9)", 720);
            OBS.obs_property_list_add_int(list, "540 lines (960x540 at 16:9)", 540);
            OBS.obs_property_list_add_int(list, "360 lines (640x360 at 16:9)", 360);
        }

        private static void AddQualityList(IntPtr properties, string name, string description)
        {
            IntPtr list = OBS.obs_properties_add_list(properties, name, description,
                OBS.obs_combo_type.OBS_COMBO_TYPE_LIST, OBS.obs_combo_format.OBS_COMBO_FORMAT_INT);
            OBS.obs_property_list_add_int(list, "Automatic (highest level a receiver asks for)", (long)OMTQuality.Default);
            OBS.obs_property_list_add_int(list, "Low", (long)OMTQuality.Low);
            OBS.obs_property_list_add_int(list, "Medium", (long)OMTQuality.Medium);
            OBS.obs_property_list_add_int(list, "High", (long)OMTQuality.High);
        }

        public override nint GetProperties()
        {
            try
            {
                IntPtr properties = OBS.obs_properties_create();
                if (properties != IntPtr.Zero)
                {
                    OBS.obs_properties_set_flags(properties, OBS.OBS_PROPERTIES_DEFER_UPDATE);
                    // Two matching boxes, each switched on by the checkbox in its title. The setting
                    // keys are unchanged, so earlier saved settings still apply.
                    IntPtr main = OBS.obs_properties_create();
                    OBS.obs_properties_add_text(main, "nameProperty", "Name", 0);
                    AddSizeList(main, "outputHeightProperty", "Size");
                    AddQualityList(main, "outputQualityProperty", "Quality");
                    OBS.obs_properties_add_group(properties, "enabledProperty", "Main Output", OBS.obs_group_type.OBS_GROUP_CHECKABLE, main);

                    IntPtr preview = OBS.obs_properties_create();
                    OBS.obs_properties_add_text(preview, "previewNameProperty", "Name", 0);
                    AddSizeList(preview, "previewHeightProperty", "Size");
                    AddQualityList(preview, "previewQualityProperty", "Quality");
                    OBS.obs_properties_add_group(properties, "previewEnabledProperty", "Preview Output", OBS.obs_group_type.OBS_GROUP_CHECKABLE, preview);
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
