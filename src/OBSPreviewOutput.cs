/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*
* Permission is hereby granted, free of charge, to any person obtaining a copy
* of this software and associated documentation files (the "Software"), to deal
* in the Software without restriction, including without limitation the rights
* to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
* copies of the Software, and to permit persons to whom the Software is
* furnished to do so, subject to the following conditions:
*
* The above copyright notice and this permission notice shall be included in all
* copies or substantial portions of the Software.
*
* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
* AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
* SOFTWARE.
*
*/

using libomtnet;
using System;
using System.Runtime.InteropServices;

namespace omtplugin
{
    /// <summary>
    /// A second OMT sender that carries the Studio Mode preview scene (or the current scene when
    /// Studio Mode is off). OBS has no built-in output for the preview, so this renders the preview
    /// scene through its own obs_view at the main output's size and format, and feeds that video
    /// to a video-only OBS output whose raw frames are sent with OMT.
    /// </summary>
    internal class OBSPreviewOutput : OBSBase
    {
        private static OBS.obs_function_create createDelegate;
        private static OBS.obs_function_destroy destroyDelegate;
        private static OBS.obs_function_get_name getNameDelegate;
        private static OBS.obs_function_start startDelegate;
        private static OBS.obs_function_stop stopDelegate;
        private static OBS.obs_function_raw_video rawVideoDelegate;
        private static OBS.obs_function_raw_audio rawAudioDelegate;
        private static OBS.obs_function_update updateDelegate;
        private static OBS.obs_function_get_properties getPropertiesDelegate;
        private static OBS.obs_function_get_defaults getDefaultsDelegate;
        private static OBS.obs_function_encoded_packet encodedPacketDelegate;

        private static OBS.obs_output_info info;

        private const string OutputIDString = "omtpreviewoutput";
        private static IntPtr OutputID = Marshal.StringToCoTaskMemUTF8(OutputIDString);

        public const string DEFAULT_PREVIEW_NAME = "OBS Preview";

        private static IntPtr previewInstance = IntPtr.Zero;
        private static IntPtr view = IntPtr.Zero;
        private static IntPtr viewVideo = IntPtr.Zero;
        private static IntPtr currentSource = IntPtr.Zero;
        private static object viewSync = new object();

        private static OBSOutputSettings? outputSettings = null;

        static OBSPreviewOutput()
        {
            createDelegate = new OBS.obs_function_create(Create);
            destroyDelegate = new OBS.obs_function_destroy(Destroy);
            getNameDelegate = new OBS.obs_function_get_name(GetName);
            startDelegate = new OBS.obs_function_start(Start);
            stopDelegate = new OBS.obs_function_stop(Stop);
            rawVideoDelegate = new OBS.obs_function_raw_video(RawVideo);
            rawAudioDelegate = new OBS.obs_function_raw_audio(RawAudio);
            getPropertiesDelegate = new OBS.obs_function_get_properties(GetProperties);
            updateDelegate = new OBS.obs_function_update(Update);
            getDefaultsDelegate = new OBS.obs_function_get_defaults(GetDefaults);
            encodedPacketDelegate = new OBS.obs_function_encoded_packet(EncodedPacket);

            info.id = OutputID;
            info.flags = OBS.OBS_OUTPUT_VIDEO;
            info.get_name = Marshal.GetFunctionPointerForDelegate(getNameDelegate);
            info.create = Marshal.GetFunctionPointerForDelegate(createDelegate);
            info.destroy = Marshal.GetFunctionPointerForDelegate(destroyDelegate);
            info.start = Marshal.GetFunctionPointerForDelegate(startDelegate);
            info.stop = Marshal.GetFunctionPointerForDelegate(stopDelegate);
            info.raw_audio = Marshal.GetFunctionPointerForDelegate(rawAudioDelegate);
            info.raw_video = Marshal.GetFunctionPointerForDelegate(rawVideoDelegate);
            info.get_properties = Marshal.GetFunctionPointerForDelegate(getPropertiesDelegate);
            info.get_defaults = Marshal.GetFunctionPointerForDelegate(getDefaultsDelegate);
            info.update = Marshal.GetFunctionPointerForDelegate(updateDelegate);
            info.encoded_packet = Marshal.GetFunctionPointerForDelegate(encodedPacketDelegate);
        }

        private IntPtr output;
        private IntPtr settings;
        private OMTSend? send;
        private OMTMediaFrame videoFrame;
        private object lockSync = new object();
        private IntPtr instance;
        private int strideHeight = 0;
        private OBS.video_format sourceFormat = 0;
        private IntPtr tempData = IntPtr.Zero;

        public OBSPreviewOutput(IntPtr output, IntPtr settings)
        {
            this.output = output;
            this.settings = settings;
            GCHandle handle = GCHandle.Alloc(this);
            instance = GCHandle.ToIntPtr(handle);
        }

        public IntPtr ToIntPtr()
        {
            return instance;
        }

        public static OBSPreviewOutput? FromIntPtr(IntPtr instance)
        {
            if (instance != IntPtr.Zero)
            {
                GCHandle handle = GCHandle.FromIntPtr(instance);
                if (handle.IsAllocated)
                {
                    if (handle.Target != null)
                    {
                        return (OBSPreviewOutput)handle.Target;
                    }
                }
            }
            return null;
        }

        public static void Register(OBSOutputSettings settings)
        {
            try
            {
                outputSettings = settings;
                OBS.obs_register_output_s(ref info, Marshal.SizeOf<OBS.obs_output_info>());
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTPreviewOutput.Register");
            }
        }

        /// <summary>
        /// Called whenever the settings change: tears the preview output down and, when it is
        /// enabled, builds it again with the current name.
        /// </summary>
        public static void UpdatePreviewOutput()
        {
            if (outputSettings != null)
            {
                DestroyPreviewOutput();
                if (outputSettings.PreviewEnabled)
                {
                    CreatePreviewOutput();
                    if (previewInstance != IntPtr.Zero)
                    {
                        OBSOutput.StartInstance(previewInstance);
                    }
                }
            }
        }

        private static void CreatePreviewOutput()
        {
            try
            {
                lock (viewSync)
                {
                    OBS.obs_video_info ovi = new OBS.obs_video_info();
                    if (!OBS.obs_get_video_info(ref ovi))
                    {
                        OMTLogging.Write("No video info available", "OMTPreviewOutput");
                        return;
                    }
                    // The view renders straight to the chosen size, so nothing is scaled afterwards.
                    if (outputSettings != null && OBSOutputSettings.TryGetScaledSize(outputSettings.PreviewHeight, out UInt32 scaledWidth, out UInt32 scaledHeight))
                    {
                        ovi.output_width = scaledWidth;
                        ovi.output_height = scaledHeight;
                        OMTLogging.Write("CreatePreviewOutput: rendering at " + scaledWidth + "x" + scaledHeight, "OMTPreviewOutput");
                    }
                    view = OBS.obs_view_create();
                    if (view == IntPtr.Zero)
                    {
                        OMTLogging.Write("obs_view_create failed", "OMTPreviewOutput");
                        return;
                    }
                    viewVideo = OBS.obs_view_add2(view, ref ovi);
                    if (viewVideo == IntPtr.Zero)
                    {
                        OMTLogging.Write("obs_view_add2 failed", "OMTPreviewOutput");
                        OBS.obs_view_destroy(view);
                        view = IntPtr.Zero;
                        return;
                    }
                    UpdateViewSourceLocked();

                    IntPtr data = OBS.obs_data_create();
                    previewInstance = OBS.obs_output_create(OutputIDString, "OMT Preview Output", data, IntPtr.Zero);
                    OBS.obs_data_release(data);
                    if (previewInstance != IntPtr.Zero)
                    {
                        OBS.obs_output_set_media(previewInstance, viewVideo, IntPtr.Zero);
                        OMTLogging.Write("CreatePreviewOutput: " + OutputIDString + "," + previewInstance, "OMTPreviewOutput");
                    }
                }
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTPreviewOutput.CreatePreviewOutput");
            }
        }

        private static void DestroyPreviewOutput()
        {
            try
            {
                lock (viewSync)
                {
                    if (previewInstance != IntPtr.Zero)
                    {
                        OBSOutput.StopInstance(previewInstance);
                        OBS.obs_output_release(previewInstance);
                        previewInstance = IntPtr.Zero;
                    }
                    if (view != IntPtr.Zero)
                    {
                        OBS.obs_view_remove(view);
                        OBS.obs_view_set_source(view, 0, IntPtr.Zero);
                        OBS.obs_view_destroy(view);
                        view = IntPtr.Zero;
                        viewVideo = IntPtr.Zero;
                    }
                    if (currentSource != IntPtr.Zero)
                    {
                        OBS.obs_source_release(currentSource);
                        currentSource = IntPtr.Zero;
                    }
                    OMTLogging.Write("DestroyPreviewOutput", "OMTPreviewOutput");
                }
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTPreviewOutput.DestroyPreviewOutput");
            }
        }

        /// <summary>
        /// Points the view at the Studio Mode preview scene, or at the current scene when Studio
        /// Mode is off, so the preview feed always shows what the operator is about to switch to.
        /// </summary>
        private static void UpdateViewSourceLocked()
        {
            if (view == IntPtr.Zero) return;
            IntPtr source;
            if (OBS.obs_frontend_preview_program_mode_active())
            {
                source = OBS.obs_frontend_get_current_preview_scene();
            }
            else
            {
                source = OBS.obs_frontend_get_current_scene();
            }
            OBS.obs_view_set_source(view, 0, source);
            if (currentSource != IntPtr.Zero)
            {
                OBS.obs_source_release(currentSource);
            }
            // The frontend functions return a new reference, which the view does not take over.
            currentSource = source;
        }

        public static void EventCallback(OBS.obs_frontend_event evt)
        {
            try
            {
                switch (evt)
                {
                    case OBS.obs_frontend_event.OBS_FRONTEND_EVENT_PREVIEW_SCENE_CHANGED:
                    case OBS.obs_frontend_event.OBS_FRONTEND_EVENT_SCENE_CHANGED:
                    case OBS.obs_frontend_event.OBS_FRONTEND_EVENT_STUDIO_MODE_ENABLED:
                    case OBS.obs_frontend_event.OBS_FRONTEND_EVENT_STUDIO_MODE_DISABLED:
                        lock (viewSync)
                        {
                            UpdateViewSourceLocked();
                        }
                        break;
                    case OBS.obs_frontend_event.OBS_FRONTEND_EVENT_SCENE_COLLECTION_CLEANUP:
                    case OBS.obs_frontend_event.OBS_FRONTEND_EVENT_EXIT:
                    case OBS.obs_frontend_event.OBS_FRONTEND_EVENT_PROFILE_CHANGING:
                        DestroyPreviewOutput();
                        break;
                }
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTPreviewOutput.EventCallback");
            }
        }

        public bool StartOutput()
        {
            try
            {
                OMTLogging.Write("StartOutput: " + this.output.ToString(), "OMTPreviewOutput");
                lock (lockSync)
                {
                    if (send == null)
                    {
                        IntPtr video = OBS.obs_output_video(this.output);
                        if (video == IntPtr.Zero)
                        {
                            OMTLogging.Write("No video context", "OMTPreviewOutput");
                            return false;
                        }
                        videoFrame = new OMTMediaFrame();
                        videoFrame.Type = OMTFrameType.Video;
                        videoFrame.Width = (int)OBS.video_output_get_width(video);
                        videoFrame.Height = (int)OBS.video_output_get_height(video);
                        videoFrame.FrameRate = (float)OBS.video_output_get_frame_rate(video);
                        OBS.video_format fmt = OBS.video_output_get_format(video);
                        OMTLogging.Write("OMTPreviewOutputFormat: " + videoFrame.Width + "x" + videoFrame.Height + " " + videoFrame.FrameRate.ToString() + " fps " + fmt.ToString(), "OMTPreviewOutput");

                        if (fmt == OBS.video_format.VIDEO_FORMAT_NV12)
                        {
                            videoFrame.Codec = (int)OMTCodec.NV12;
                            strideHeight = (int)(videoFrame.Height * 1.5F);
                        }
                        else if (fmt == OBS.video_format.VIDEO_FORMAT_UYVY)
                        {
                            videoFrame.Codec = (int)OMTCodec.UYVY;
                            strideHeight = videoFrame.Height;
                        }
                        else if (fmt == OBS.video_format.VIDEO_FORMAT_YUY2)
                        {
                            videoFrame.Codec = (int)OMTCodec.YUY2;
                            strideHeight = videoFrame.Height;
                        }
                        else if (fmt == OBS.video_format.VIDEO_FORMAT_BGRA)
                        {
                            videoFrame.Flags = OMTVideoFlags.Alpha | OMTVideoFlags.PreMultiplied;
                            videoFrame.Codec = (int)OMTCodec.BGRA;
                            strideHeight = videoFrame.Height;
                        }
                        else if (fmt == OBS.video_format.VIDEO_FORMAT_BGRX)
                        {
                            videoFrame.Codec = (int)OMTCodec.BGRA;
                            videoFrame.Flags = OMTVideoFlags.None;
                            strideHeight = videoFrame.Height;
                        }
                        else if (fmt == OBS.video_format.VIDEO_FORMAT_P216 || fmt == OBS.video_format.VIDEO_FORMAT_P010)
                        {
                            videoFrame.Codec = (int)OMTCodec.P216;
                            videoFrame.Flags = OMTVideoFlags.HighBitDepth;
                            strideHeight = videoFrame.Height * 2;
                        }
                        else
                        {
                            OMTLogging.Write("Video Format not supported: " + fmt.ToString(), "OMTPreviewOutput");
                            return false;
                        }
                        sourceFormat = fmt;

                        string? name = DEFAULT_PREVIEW_NAME;
                        if (outputSettings != null)
                        {
                            if (!String.IsNullOrEmpty(outputSettings.PreviewName))
                            {
                                name = outputSettings.PreviewName;
                            }
                        }
                        OMTQuality quality = outputSettings != null ? outputSettings.PreviewQuality : OMTQuality.Default;
                        send = new OMTSend(name, quality);
                        OBS.obs_output_begin_data_capture(this.output, 0);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTPreviewOutput.StartOutput");
            }
            return false;
        }

        public void StopOutput()
        {
            try
            {
                OMTLogging.Write("StopOutput: " + this.output.ToString(), "OMTPreviewOutput");
                lock (lockSync)
                {
                    if (this.output != IntPtr.Zero)
                    {
                        OBS.obs_output_end_data_capture(this.output);
                    }
                    if (send != null)
                    {
                        if (!OBSOutput.Exiting)
                        {
                            send.Dispose();
                        }
                        send = null;
                    }
                    if (tempData != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(tempData);
                        tempData = IntPtr.Zero;
                    }
                }
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTPreviewOutput.StopOutput");
            }
        }

        public void ProcessVideo(ref OBS.video_data frame)
        {
            if (frame.data != null)
            {
                if (frame.data[0] != IntPtr.Zero)
                {
                    videoFrame.Stride = (int)frame.linesize[0];
                    if (sourceFormat == OBS.video_format.VIDEO_FORMAT_P010)
                    {
                        if (tempData == IntPtr.Zero)
                        {
                            int len = videoFrame.Stride * strideHeight;
                            tempData = Marshal.AllocHGlobal(len);
                        }
                        Utils.P010ToP216(frame.data[0], (int)frame.linesize[0], frame.data[1], (int)frame.linesize[1], tempData, videoFrame.Stride, videoFrame.Width, videoFrame.Height);
                        videoFrame.Data = tempData;
                    }
                    else
                    {
                        videoFrame.Data = frame.data[0];
                    }
                    videoFrame.Timestamp = (Int64)frame.timestamp / 100;
                    videoFrame.DataLength = videoFrame.Stride * strideHeight;
                    lock (lockSync)
                    {
                        if (send != null)
                        {
                            send.Send(videoFrame);
                        }
                    }
                }
            }
        }

        private static void RawVideo(IntPtr data, ref OBS.video_data frame)
        {
            try
            {
                OBSPreviewOutput? output = FromIntPtr(data);
                if (output != null)
                {
                    output.ProcessVideo(ref frame);
                }
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTPreviewOutput.RawVideo");
            }
        }

        private static void RawAudio(IntPtr data, ref OBS.audio_data frames)
        {
            // A video-only output never receives audio.
        }

        private static void EncodedPacket(IntPtr data, IntPtr packet)
        {
        }

        private static bool Start(IntPtr data)
        {
            OBSPreviewOutput? output = FromIntPtr(data);
            if (output != null)
            {
                return output.StartOutput();
            }
            return false;
        }

        private static void Stop(IntPtr data, UInt64 ts)
        {
            OBSPreviewOutput? output = FromIntPtr(data);
            if (output != null)
            {
                output.StopOutput();
            }
        }

        private static IntPtr GetName(IntPtr type_data)
        {
            return Marshal.StringToHGlobalAnsi("OMT Preview Output");
        }

        private static void GetDefaults(IntPtr settings)
        {
        }

        private static IntPtr GetProperties(IntPtr data)
        {
            return IntPtr.Zero;
        }

        private static void Update(IntPtr data, IntPtr settings)
        {
        }

        private static IntPtr Create(IntPtr settings, IntPtr source)
        {
            try
            {
                OBSPreviewOutput output = new OBSPreviewOutput(source, settings);
                IntPtr data = output.ToIntPtr();
                OMTLogging.Write("Create: Data: " + data.ToString() + " Source: " + source.ToString(), "OMTPreviewOutput");
                return data;
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTPreviewOutput.Create");
            }
            return IntPtr.Zero;
        }

        private static void Destroy(IntPtr data)
        {
            try
            {
                GCHandle handle = GCHandle.FromIntPtr(data);
                if (handle.IsAllocated)
                {
                    OBSPreviewOutput? output = (OBSPreviewOutput?)handle.Target;
                    if (output != null)
                    {
                        output.Dispose();
                    }
                    handle.Free();
                }
            }
            catch (Exception ex)
            {
                OMTLogging.Write(ex.ToString(), "OMTPreviewOutput.Destroy");
            }
        }

        protected override void DisposeInternal()
        {
            if (this.send != null)
            {
                this.send.Dispose();
                this.send = null;
            }
            if (instance != IntPtr.Zero)
            {
                GCHandle handle = GCHandle.FromIntPtr(instance);
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
                instance = IntPtr.Zero;
            }
            base.DisposeInternal();
        }
    }
}
