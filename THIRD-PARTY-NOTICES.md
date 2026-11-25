# Third-party notices

## FFmpeg (worker container image)

The worker image ships a static build of [FFmpeg](https://ffmpeg.org) 9.1 from
[wader/static-ffmpeg](https://github.com/wader/static-ffmpeg), pinned by digest in
`src/ConvertApi.Worker/Dockerfile`. That build includes GPL components (x264, x265 and others),
so the FFmpeg binary is distributed under the
[GNU General Public License, version 3](https://www.gnu.org/licenses/gpl-3.0.html).

ConvertAPI runs FFmpeg as a separate process and does not link to it. The corresponding source
is available from the FFmpeg project and from wader/static-ffmpeg at the pinned version.

## ImageMagick (Magick.NET)

Image conversions use [Magick.NET](https://github.com/dlemstra/Magick.NET), which bundles
ImageMagick under the [Apache License 2.0](https://github.com/dlemstra/Magick.NET/blob/main/License.txt)
and the [ImageMagick License](https://imagemagick.org/script/license.php).
