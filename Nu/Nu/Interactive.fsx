﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2017.

#I __SOURCE_DIRECTORY__
#r "System.Configuration"
#r "../../Prime/Prime.Dependencies/System.ValueTuple.4.3.0/lib/portable-net40+sl4+win8+wp8/System.ValueTuple.dll"
#r "../../Prime/Prime.Dependencies/FParsec/FParsecCS.dll" // MUST be referenced BEFORE FParsec.dll!
#r "../../Prime/Prime.Dependencies/FParsec/FParsec.dll"
#r "../../Prime/Prime.Dependencies/xunit/xunit.core.dll"
#r "../../Prime/Prime.Dependencies/xunit/xunit.abstractions.dll"
#r "../../Prime/Prime.Dependencies/xunit/xunit.assert.dll"
#r "../../Prime/Prime.Dependencies/xunit/xunit.execution.desktop.dll"
#r "../../Prime/Prime.Dependencies/FsCheck/FsCheck.dll"
#r "../../Prime/Prime.Dependencies/FsCheck.Xunit/FsCheck.Xunit.dll"
#r "../../Prime/Prime/bin/Debug/Prime.exe"
#r "../../Nu/Nu.Dependencies/FSharpx.Core/FSharpx.Core.dll"
#r "../../Nu/Nu.Dependencies/FSharpx.Collections/FSharpx.Collections.dll"
#r "../../Nu/Nu.Dependencies/Farseer/FarseerPhysics.dll"
#r "../../Nu/Nu.Dependencies/Magick.NET/Magick.NET-AnyCPU.dll"
#r "../../Nu/Nu.Dependencies/Nito.Collections.Deque/Nito.Collections.Deque.dll"
#r "../../Nu/Nu.Dependencies/SDL2#/Debug/SDL2#.dll"
#r "../../Nu/Nu.Dependencies/TiledSharp/Debug/TiledSharp.dll"
#r "../../Nu/Nu.SDL2/bin/Debug/Nu.SDL2.dll"
#r "../../Nu/Nu/bin/Debug/Nu.exe"

open System
open System.IO
open FSharpx
open FSharpx.Collections
open SDL2
open OpenTK
open TiledSharp
open Prime
open Prime.Stream
open Prime.Chain
open global.Nu
open global.Nu.Stream

// set current directly to local for execution in VS F# interactive
Directory.SetCurrentDirectory (__SOURCE_DIRECTORY__ + "../bin/Debug")

// initialize Nu
Nu.init false