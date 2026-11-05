using System;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace DoorBird.App.Video;

/// <summary>
/// Records the DoorBird RTSP video and HTTP mu-law audio into a fragmented MP4
/// using the bundled libavformat/libavcodec. Video is remuxed without re-encoding
/// (the H.264 packets go straight into the file); audio is decoded from G.711
/// mu-law and encoded to AAC. This mirrors what the old ffmpeg command line did:
///   -c:v copy -c:a aac -b:a 64k -movflags +frag_keyframe+empty_moov
/// Fragmented output means a crash or power loss still leaves a playable file.
/// </summary>
public sealed unsafe class Mp4Recorder : IDisposable {
    private readonly string _videoUrl;
    private readonly string _audioUrl;
    private readonly string _outputPath;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _writeLock = new();
    // Both inputs connect at different times (RTSP handshake vs HTTP). Each stream
    // is placed on the file timeline at its real arrival time relative to this
    // clock, so audio and video line up instead of both starting at zero.
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private Thread? _videoThread;
    private Thread? _audioThread;
    private GCHandle _selfHandle;
    private static readonly AVIOInterruptCB_callback InterruptDelegate = InterruptCallback;

    private AVFormatContext* _out;
    private AVStream* _outVideo;
    private AVStream* _outAudio;
    private AVCodecContext* _aacEnc;
    private bool _headerWritten;
    private int _running;

    public event Action<string>? Error;
    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public string OutputPath => _outputPath;

    public Mp4Recorder(string videoUrl, string audioUrl, string outputPath) {
        _videoUrl = videoUrl;
        _audioUrl = audioUrl;
        _outputPath = outputPath;
    }

    /// <summary>Opens the RTSP input, prepares the file and starts both pumps. Throws on setup failure.</summary>
    public void Start() {
        if (!FfmpegNative.TryInitialize(out var err)) throw new InvalidOperationException(err);
        _selfHandle = GCHandle.Alloc(this, GCHandleType.Weak);
        Volatile.Write(ref _running, 1);
        _videoThread = new Thread(VideoPump) { IsBackground = true, Name = "record-video" };
        _videoThread.Start();
    }

    public void Stop() {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _videoThread?.Join(TimeSpan.FromSeconds(8));
        _audioThread?.Join(TimeSpan.FromSeconds(8));
        Finish();
        Volatile.Write(ref _running, 0);
    }

    // ---------------------------------------------------------------- video

    private void VideoPump() {
        AVFormatContext* input = null;
        AVDictionary* opts = null;
        AVPacket* pkt = null;
        try {
            input = ffmpeg.avformat_alloc_context();
            input->interrupt_callback.callback = (AVIOInterruptCB_callback_func)InterruptDelegate;
            input->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_selfHandle);
            ffmpeg.av_dict_set(&opts, "rtsp_transport", "tcp", 0);
            ffmpeg.av_dict_set(&opts, "timeout", "10000000", 0);
            // The SDP already carries SPS/PPS, so stream probing only needs the first
            // keyframe. The default analyzes up to 5 s before the first frame shows.
            ffmpeg.av_dict_set(&opts, "analyzeduration", "500000", 0);
            var r = ffmpeg.avformat_open_input(&input, _videoUrl, null, &opts);
            if (r < 0) { input = null; FfmpegNative.ThrowIfError(r, "open RTSP for recording"); }
            FfmpegNative.ThrowIfError(ffmpeg.avformat_find_stream_info(input, null), "stream info");
            var vIndex = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            FfmpegNative.ThrowIfError(vIndex, "find video stream");
            var inVideo = input->streams[vIndex];

            // Output file: video stream copied, audio stream AAC.
            fixed (AVFormatContext** pout = &_out)
                FfmpegNative.ThrowIfError(ffmpeg.avformat_alloc_output_context2(pout, null, "mp4", _outputPath), "create MP4");

            _outVideo = ffmpeg.avformat_new_stream(_out, null);
            FfmpegNative.ThrowIfError(ffmpeg.avcodec_parameters_copy(_outVideo->codecpar, inVideo->codecpar), "copy video params");
            _outVideo->codecpar->codec_tag = 0;
            _outVideo->time_base = inVideo->time_base;

            SetupAudioEncoder();

            FfmpegNative.ThrowIfError(ffmpeg.avio_open(&_out->pb, _outputPath, ffmpeg.AVIO_FLAG_WRITE), "open output file");
            AVDictionary* muxOpts = null;
            ffmpeg.av_dict_set(&muxOpts, "movflags", "frag_keyframe+empty_moov", 0);
            var hr = ffmpeg.avformat_write_header(_out, &muxOpts);
            ffmpeg.av_dict_free(&muxOpts);
            FfmpegNative.ThrowIfError(hr, "write MP4 header");
            _headerWritten = true;

            _audioThread = new Thread(AudioPump) { IsBackground = true, Name = "record-audio" };
            _audioThread.Start();

            pkt = ffmpeg.av_packet_alloc();
            long firstDts = ffmpeg.AV_NOPTS_VALUE;
            long lastDts = long.MinValue;
            long arrivalOffset = 0; // in input time_base units
            long videoPackets = 0;
            VideoLog.Write("recorder", $"video input open after {_clock.ElapsedMilliseconds} ms");
            while (!_cts.IsCancellationRequested) {
                r = ffmpeg.av_read_frame(input, pkt);
                if (r < 0) {
                    if (r == ffmpeg.AVERROR_EOF || _cts.IsCancellationRequested) break;
                    FfmpegNative.ThrowIfError(r, "read video");
                }
                try {
                    if (pkt->stream_index != vIndex) continue;
                    if (pkt->dts == ffmpeg.AV_NOPTS_VALUE) pkt->dts = pkt->pts;
                    if (pkt->pts == ffmpeg.AV_NOPTS_VALUE) pkt->pts = pkt->dts;
                    if (pkt->dts == ffmpeg.AV_NOPTS_VALUE) continue;
                    if (firstDts == ffmpeg.AV_NOPTS_VALUE) {
                        firstDts = pkt->dts;
                        arrivalOffset = ffmpeg.av_rescale_q(_clock.ElapsedMilliseconds,
                            new AVRational { num = 1, den = 1000 }, inVideo->time_base);
                    }
                    pkt->dts = pkt->dts - firstDts + arrivalOffset;
                    pkt->pts = pkt->pts - firstDts + arrivalOffset;
                    if (pkt->dts <= lastDts) {        // keep the muxer's monotonic requirement
                        var shift = lastDts + 1 - pkt->dts;
                        pkt->dts += shift;
                        pkt->pts += shift;
                    }
                    lastDts = pkt->dts;
                    videoPackets++;
                    ffmpeg.av_packet_rescale_ts(pkt, inVideo->time_base, _outVideo->time_base);
                    pkt->stream_index = _outVideo->index;
                    pkt->pos = -1;
                    WritePacket(pkt);
                } finally {
                    ffmpeg.av_packet_unref(pkt);
                }
            }
            VideoLog.Write("recorder", $"video pump done: {videoPackets} packets at {_clock.ElapsedMilliseconds} ms");
        } catch (Exception ex) {
            VideoLog.Write("recorder", $"video pump failed: {ex}");
            if (!_cts.IsCancellationRequested) {
                Error?.Invoke(ex.Message);
                _cts.Cancel();
            }
        } finally {
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (input != null) ffmpeg.avformat_close_input(&input);
            if (opts != null) ffmpeg.av_dict_free(&opts);
            Volatile.Write(ref _running, 0);
        }
    }

    // ---------------------------------------------------------------- audio

    private void SetupAudioEncoder() {
        var enc = ffmpeg.avcodec_find_encoder_by_name("aac");
        if (enc == null) throw new FfmpegException("AAC encoder not available", -1);
        _aacEnc = ffmpeg.avcodec_alloc_context3(enc);
        _aacEnc->sample_rate = 8000;
        _aacEnc->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        ffmpeg.av_channel_layout_default(&_aacEnc->ch_layout, 1);
        _aacEnc->bit_rate = 64000;
        _aacEnc->time_base = new AVRational { num = 1, den = 8000 };
        if ((_out->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            _aacEnc->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        FfmpegNative.ThrowIfError(ffmpeg.avcodec_open2(_aacEnc, enc, null), "open AAC encoder");

        _outAudio = ffmpeg.avformat_new_stream(_out, null);
        FfmpegNative.ThrowIfError(ffmpeg.avcodec_parameters_from_context(_outAudio->codecpar, _aacEnc), "audio params");
        _outAudio->time_base = _aacEnc->time_base;
    }

    private void AudioPump() {
        AVFormatContext* input = null;
        AVDictionary* opts = null;
        AVCodecContext* dec = null;
        SwrContext* swr = null;
        AVAudioFifo* fifo = null;
        AVPacket* pkt = null;
        AVPacket* encPkt = null;
        AVFrame* decFrame = null;
        AVFrame* cvtFrame = null;
        AVFrame* encFrame = null;
        try {
            input = ffmpeg.avformat_alloc_context();
            input->interrupt_callback.callback = (AVIOInterruptCB_callback_func)InterruptDelegate;
            input->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_selfHandle);
            var mulaw = ffmpeg.av_find_input_format("mulaw");
            if (mulaw == null) throw new FfmpegException("mulaw demuxer missing", -1);
            ffmpeg.av_dict_set(&opts, "sample_rate", "8000", 0);
            ffmpeg.av_dict_set(&opts, "ch_layout", "mono", 0);
            // Raw PCM has nothing to probe; without these the demuxer would buffer
            // seconds of audio before returning the first packet.
            ffmpeg.av_dict_set(&opts, "probesize", "32", 0);
            ffmpeg.av_dict_set(&opts, "analyzeduration", "0", 0);
            var r = ffmpeg.avformat_open_input(&input, _audioUrl, mulaw, &opts);
            if (r < 0) { input = null; FfmpegNative.ThrowIfError(r, "open audio"); }
            if (input->nb_streams < 1) throw new FfmpegException("audio input has no stream", -1);
            var aIndex = 0;

            var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_PCM_MULAW);
            dec = ffmpeg.avcodec_alloc_context3(codec);
            FfmpegNative.ThrowIfError(ffmpeg.avcodec_parameters_to_context(dec, input->streams[aIndex]->codecpar), "audio dec params");
            if (dec->ch_layout.nb_channels == 0) ffmpeg.av_channel_layout_default(&dec->ch_layout, 1);
            if (dec->sample_rate == 0) dec->sample_rate = 8000;
            FfmpegNative.ThrowIfError(ffmpeg.avcodec_open2(dec, codec, null), "open mulaw decoder");

            FfmpegNative.ThrowIfError(ffmpeg.swr_alloc_set_opts2(&swr,
                &_aacEnc->ch_layout, _aacEnc->sample_fmt, _aacEnc->sample_rate,
                &dec->ch_layout, dec->sample_fmt, dec->sample_rate, 0, null), "resampler");
            FfmpegNative.ThrowIfError(ffmpeg.swr_init(swr), "resampler init");

            var frameSize = _aacEnc->frame_size > 0 ? _aacEnc->frame_size : 1024;
            fifo = ffmpeg.av_audio_fifo_alloc(_aacEnc->sample_fmt, 1, frameSize * 4);
            pkt = ffmpeg.av_packet_alloc();
            encPkt = ffmpeg.av_packet_alloc();
            decFrame = ffmpeg.av_frame_alloc();
            cvtFrame = ffmpeg.av_frame_alloc();
            encFrame = ffmpeg.av_frame_alloc();
            encFrame->nb_samples = frameSize;
            encFrame->format = (int)_aacEnc->sample_fmt;
            encFrame->sample_rate = _aacEnc->sample_rate;
            ffmpeg.av_channel_layout_copy(&encFrame->ch_layout, &_aacEnc->ch_layout);
            FfmpegNative.ThrowIfError(ffmpeg.av_frame_get_buffer(encFrame, 0), "audio frame buffer");
            long samplesOut = -1; // set from the wall clock when the first audio arrives
            long audioPackets = 0, audioFrames = 0;
            VideoLog.Write("recorder", $"audio input open after {_clock.ElapsedMilliseconds} ms");

            while (!_cts.IsCancellationRequested) {
                r = ffmpeg.av_read_frame(input, pkt);
                if (r < 0) {
                    VideoLog.Write("recorder", $"audio read ended: {FfmpegNative.ErrorString(r)} after {audioPackets} packets / {audioFrames} encoded frames at {_clock.ElapsedMilliseconds} ms");
                    if (r == ffmpeg.AVERROR_EOF || _cts.IsCancellationRequested) break;
                    FfmpegNative.ThrowIfError(r, "read audio");
                }
                try {
                    if (pkt->stream_index != aIndex) continue;
                    audioPackets++;
                    r = ffmpeg.avcodec_send_packet(dec, pkt);
                    if (r < 0) { VideoLog.Write("recorder", $"audio decode send failed: {FfmpegNative.ErrorString(r)}"); continue; }
                    while (ffmpeg.avcodec_receive_frame(dec, decFrame) >= 0) {
                        if (samplesOut < 0) samplesOut = _clock.ElapsedMilliseconds * _aacEnc->sample_rate / 1000;
                        ffmpeg.av_frame_unref(cvtFrame);
                        cvtFrame->format = (int)_aacEnc->sample_fmt;
                        cvtFrame->sample_rate = _aacEnc->sample_rate;
                        ffmpeg.av_channel_layout_copy(&cvtFrame->ch_layout, &_aacEnc->ch_layout);
                        r = ffmpeg.swr_convert_frame(swr, cvtFrame, decFrame);
                        if (r < 0) { VideoLog.Write("recorder", $"audio resample failed: {FfmpegNative.ErrorString(r)}"); continue; }
                        if (cvtFrame->nb_samples > 0)
                            ffmpeg.av_audio_fifo_write(fifo, (void**)cvtFrame->extended_data, cvtFrame->nb_samples);
                        ffmpeg.av_frame_unref(decFrame);

                        while (ffmpeg.av_audio_fifo_size(fifo) >= frameSize) {
                            FfmpegNative.ThrowIfError(ffmpeg.av_frame_make_writable(encFrame), "audio frame writable");
                            ffmpeg.av_audio_fifo_read(fifo, (void**)encFrame->extended_data, frameSize);
                            encFrame->pts = samplesOut;
                            samplesOut += frameSize;
                            audioFrames++;
                            EncodeAudio(encFrame, encPkt);
                        }
                    }
                } finally {
                    ffmpeg.av_packet_unref(pkt);
                }
            }
            // Flush the encoder so the last fraction of a second is not lost.
            EncodeAudio(null, encPkt);
            VideoLog.Write("recorder", $"audio pump done: {audioPackets} packets, {audioFrames} frames, cancelled={_cts.IsCancellationRequested}");
        } catch (Exception ex) {
            VideoLog.Write("recorder", $"audio pump failed: {ex}");
            // Audio is best-effort: a failed audio connection must not kill the video recording.
            if (!_cts.IsCancellationRequested) Error?.Invoke($"audio: {ex.Message}");
        } finally {
            if (encFrame != null) ffmpeg.av_frame_free(&encFrame);
            if (cvtFrame != null) ffmpeg.av_frame_free(&cvtFrame);
            if (decFrame != null) ffmpeg.av_frame_free(&decFrame);
            if (encPkt != null) ffmpeg.av_packet_free(&encPkt);
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (fifo != null) ffmpeg.av_audio_fifo_free(fifo);
            if (swr != null) ffmpeg.swr_free(&swr);
            if (dec != null) ffmpeg.avcodec_free_context(&dec);
            if (input != null) ffmpeg.avformat_close_input(&input);
            if (opts != null) ffmpeg.av_dict_free(&opts);
        }
    }

    private void EncodeAudio(AVFrame* frame, AVPacket* encPkt) {
        var r = ffmpeg.avcodec_send_frame(_aacEnc, frame);
        if (r < 0) return;
        while (true) {
            r = ffmpeg.avcodec_receive_packet(_aacEnc, encPkt);
            if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF) break;
            if (r < 0) break;
            ffmpeg.av_packet_rescale_ts(encPkt, _aacEnc->time_base, _outAudio->time_base);
            encPkt->stream_index = _outAudio->index;
            WritePacket(encPkt);
            ffmpeg.av_packet_unref(encPkt);
        }
    }

    // ---------------------------------------------------------------- output

    private void WritePacket(AVPacket* pkt) {
        lock (_writeLock) {
            if (_out == null || !_headerWritten) return;
            ffmpeg.av_interleaved_write_frame(_out, pkt);
        }
    }

    private void Finish() {
        lock (_writeLock) {
            if (_out != null) {
                if (_headerWritten) ffmpeg.av_write_trailer(_out);
                if (_out->pb != null) ffmpeg.avio_closep(&_out->pb);
                fixed (AVFormatContext** p = &_out) ffmpeg.avformat_free_context(*p);
                _out = null;
                _headerWritten = false;
            }
            if (_aacEnc != null) fixed (AVCodecContext** p = &_aacEnc) ffmpeg.avcodec_free_context(p);
        }
    }

    private static int InterruptCallback(void* opaque) {
        try {
            var self = GCHandle.FromIntPtr((IntPtr)opaque).Target as Mp4Recorder;
            return self == null || self._cts.IsCancellationRequested ? 1 : 0;
        } catch { return 1; }
    }

    public void Dispose() {
        Stop();
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        _cts.Dispose();
    }
}
