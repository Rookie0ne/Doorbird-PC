using System;
using System.IO;
using System.Threading;
using DoorBird.App.Video;
using FFmpeg.AutoGen;

if (args.Length < 3) {
    Console.Error.WriteLine("usage: DoorBird.VideoSmoke <host> <user> <password> [seconds]");
    return 2;
}
var host = args[0];
var user = Uri.EscapeDataString(args[1]);
var pass = Uri.EscapeDataString(args[2]);
var seconds = args.Length > 3 ? int.Parse(args[3]) : 8;

if (!FfmpegNative.TryInitialize(out var err)) {
    Console.Error.WriteLine($"FFmpeg not available: {err}");
    return 1;
}
Console.WriteLine($"FFmpeg: {FfmpegNative.VersionString}");

var rtsp = $"rtsp://{user}:{pass}@{host}/mpeg/media.amp";
var audio = $"http://{user}:{pass}@{host}/bha-api/audio-receive.cgi";
var output = Path.Combine(Path.GetTempPath(), $"doorbird-smoke-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");

using (var rec = new Mp4Recorder(rtsp, audio, output)) {
    rec.Error += m => Console.WriteLine($"recorder error: {m}");
    rec.Start();
    Console.WriteLine($"recording {seconds}s to {output} ...");
    for (var i = 0; i < seconds; i++) {
        Thread.Sleep(1000);
        if (!rec.IsRunning) { Console.WriteLine("recorder stopped early"); break; }
    }
    rec.Stop();
}

return Probe(output);

static unsafe int Probe(string path) {
    AVFormatContext* fmt = null;
    var r = ffmpeg.avformat_open_input(&fmt, path, null, null);
    if (r < 0) { Console.Error.WriteLine($"open failed: {FfmpegNative.ErrorString(r)}"); return 1; }
    r = ffmpeg.avformat_find_stream_info(fmt, null);
    if (r < 0) { Console.Error.WriteLine($"stream info failed: {FfmpegNative.ErrorString(r)}"); return 1; }
    Console.WriteLine($"file: {new FileInfo(path).Length / 1024} KiB, duration {fmt->duration / 1_000_000.0:F2}s, {fmt->nb_streams} streams");

    // Fragmented MP4 keeps sample tables in the fragments, so nb_frames is 0 in
    // the header; count packets by actually reading the file.
    var packets = new long[fmt->nb_streams];
    var bytes = new long[fmt->nb_streams];
    var lastPts = new long[fmt->nb_streams];
    var pkt = ffmpeg.av_packet_alloc();
    while (ffmpeg.av_read_frame(fmt, pkt) >= 0) {
        packets[pkt->stream_index]++;
        bytes[pkt->stream_index] += pkt->size;
        lastPts[pkt->stream_index] = pkt->pts;
        ffmpeg.av_packet_unref(pkt);
    }
    ffmpeg.av_packet_free(&pkt);

    var ok = true;
    for (var i = 0; i < fmt->nb_streams; i++) {
        var s = fmt->streams[i];
        var p = s->codecpar;
        var name = ffmpeg.avcodec_get_name(p->codec_id);
        var secs = lastPts[i] * s->time_base.num / (double)s->time_base.den;
        if (p->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            Console.WriteLine($"  video: {name} {p->width}x{p->height}, {packets[i]} packets, {bytes[i] / 1024} KiB, last pts {secs:F2}s");
        else if (p->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
            Console.WriteLine($"  audio: {name} {p->sample_rate} Hz, {p->ch_layout.nb_channels} ch, {packets[i]} packets, {bytes[i] / 1024} KiB, last pts {secs:F2}s");
        if (packets[i] < 10) ok = false;
    }
    ffmpeg.avformat_close_input(&fmt);
    Console.WriteLine(ok ? "OK" : "FAIL: a stream has too few packets");
    return ok ? 0 : 1;
}
