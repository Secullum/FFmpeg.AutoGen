using System;

namespace FFmpeg.AutoGen.Simple
{
    public sealed unsafe class AudioFrameConverter : IDisposable
    {
        private AVCodecContext* _pOutCtx;
        private AVCodecContext* _pInCtx;

        private AVFilterGraph* _pFilterGraph;
        private AVFilterContext* _pBufferSrcCtx;
        private AVFilterContext* _pBufferSinkCtx;

        public AudioFrameConverter(AVCodecContext* outputCtx, AVCodecContext* inputCtx)
        {
            _pOutCtx = outputCtx;
            _pInCtx = inputCtx;

            ffmpeg.avfilter_register_all();
            InitFilters();
        }

        public void Dispose()
        {
            ffmpeg.avfilter_free(_pBufferSinkCtx);
            ffmpeg.avfilter_free(_pBufferSrcCtx);

            fixed (AVFilterGraph** ptr = &_pFilterGraph)
            {
                ffmpeg.avfilter_graph_free(ptr);
            }
        }

        public int Convert(AVFrame* frame, Func<AVFrame, int> writeFunc)
        {
            int ret;
            AVFrame* filteredFrame = null;

            ffmpeg.av_buffersrc_write_frame(_pBufferSrcCtx, frame).ThrowExceptionIfError();

            while (true)
            {
                filteredFrame = ffmpeg.av_frame_alloc();

                if (filteredFrame == null)
                {
                    throw new Exception("Could not allocate filtered frame");
                }

                ret = ffmpeg.av_buffersink_get_frame(_pBufferSinkCtx, filteredFrame);

                if (ret < 0)
                {
                    if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    {
                        ret = 0;
                    }

                    ffmpeg.av_frame_free(&filteredFrame);
                    break;
                }

                filteredFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;

                ret = writeFunc(*filteredFrame); //Passo o valor pois no c# é proibido usar ponteiro como tipo genérico

                if (ret < 0)
                {
                    break;
                }
            }

            ffmpeg.av_frame_free(&filteredFrame);

            return ret;
        }

        private void InitFilters()
        {
            var encoderSampleFmt = ffmpeg.av_get_sample_fmt_name(_pOutCtx->sample_fmt);
            var outFilter = $"aresample={_pOutCtx->sample_rate},aformat=sample_fmts={encoderSampleFmt}:" +
                $"channel_layouts=stereo,asetnsamples=n={_pOutCtx->frame_size}:p=0";

            string args;
            AVFilter* abuffersrc = ffmpeg.avfilter_get_by_name("abuffer");
            AVFilter* abuffersink = ffmpeg.avfilter_get_by_name("abuffersink");
            AVFilterInOut* outputs = ffmpeg.avfilter_inout_alloc();
            AVFilterInOut* inputs = ffmpeg.avfilter_inout_alloc();
            _pFilterGraph = ffmpeg.avfilter_graph_alloc();

            if (outputs == null || inputs == null || _pFilterGraph == null || abuffersrc == null || abuffersink == null)
            {
                throw new Exception("Could not allocate filter resources");
            }

            if (_pInCtx->channel_layout == 0)
            {
                _pInCtx->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(_pInCtx->channels);
            }

            args = string.Format("time_base={0}/{1}:sample_rate={2}:sample_fmt={3}:channel_layout=0x{4:X}",
                        _pInCtx->time_base.num, _pInCtx->time_base.den, _pInCtx->sample_rate,
                        ffmpeg.av_get_sample_fmt_name(_pInCtx->sample_fmt),
                        _pInCtx->channel_layout);

            fixed (AVFilterContext** ptr = &_pBufferSrcCtx)
            {
                ffmpeg.avfilter_graph_create_filter(ptr, abuffersrc, "in", args, null, _pFilterGraph).ThrowExceptionIfError();
            }

            fixed (AVFilterContext** ptr = &_pBufferSinkCtx)
            {
                ffmpeg.avfilter_graph_create_filter(ptr, abuffersink, "out", null, null, _pFilterGraph).ThrowExceptionIfError();
            }

            ffmpeg.av_opt_set_bin(_pBufferSinkCtx, "sample_fmts", (byte*)&_pOutCtx->sample_fmt, sizeof(AVSampleFormat),
                ffmpeg.AV_OPT_SEARCH_CHILDREN).ThrowExceptionIfError();

            ffmpeg.av_opt_set_bin(_pBufferSinkCtx, "channel_layouts", (byte*)&_pOutCtx->channel_layout, sizeof(ulong),
                ffmpeg.AV_OPT_SEARCH_CHILDREN);

            ffmpeg.av_opt_set_bin(_pBufferSinkCtx, "sample_rates", (byte*)&_pOutCtx->sample_rate, sizeof(int),
                ffmpeg.AV_OPT_SEARCH_CHILDREN);

            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = _pBufferSrcCtx;
            outputs->pad_idx = 0;
            outputs->next = null;

            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = _pBufferSinkCtx;
            inputs->pad_idx = 0;
            inputs->next = null;

            ffmpeg.avfilter_graph_parse_ptr(_pFilterGraph, outFilter,
                                            &inputs, &outputs, null).ThrowExceptionIfError();

            ffmpeg.avfilter_graph_config(_pFilterGraph, null).ThrowExceptionIfError();
        }
    }
}
