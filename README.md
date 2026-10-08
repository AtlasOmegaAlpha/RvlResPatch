# RvlResPatch
Wii BRRES/MDL0 patcher and optimizer written in .NET 8.

## Features
- Automatically patches issues found in MDL0 files saved by BrawlBox/BrawlCrate:
  - dummy fur vector entries
  - vertex attributes from non-float formats
  - invalid texture matrices
- Allows optimizing models by choosing better vertex formats
- Primitive editing to allow wireframe and point rendering modes
- Contains an interactive editor that allows changing data for individual polygons/models

## Usage
```
RvlResPatch [options] <file1.brres|file1.mdl0> [<file2.brres|file2.mdl0>, etc]

Options:
 -o <path>			output path. Default: <name>_patched.<ext>
 --overwrite		overwrite the input file
 --format <type>    converts vertex data to the specified format
						Supported formats per component:
							positions/UVs: 	u8, s8, u16, s16, f32
                            normals:		s8, s16, f32
                            colors:       	rgb565, rgb8, rgbx8, rgba4, rgba6, rgba8
 --divisor <value>	(used along with --format) use this divisor for fixed formats,
                    otherwise find the most optimal one by default. Must be a power of 2
 --optimize [mode]	convert vertex data to a smaller format:
							nw4r (default): pick formats with the 'auto' quantization mode
											from nw4r_g3dcvtr
							lossless:		only formats that keep every value the same
 --primitive <type>	convert the polygons' primitives:
							triangles, tristrips, trifans, quads, lines, linestrips, points
 --only <list>		limit --format/--optimize/--primitive (comma separated):
							arrays:		position, normal, color, uv, name or type:name
										(e.g. 'position:mario_body__mario_all_tx')
							polygons:	polygons, polygon:name or model/polygon ('*' = any)
										(e.g. 'polygon:polygon0', 'model_lod/*')
 -e, --format-edit	open the interactive editor to modify files (only one at a time)
```
