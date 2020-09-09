# Unity Video Streaming Server Package



## Introduction

This is a Unity 3D package for live video streaming using RTP and the server here includes RTSP server which wraps native platform APIs for performing H.264 encoding and RTP streaming.



## Utilization

1. Replace `Packages` folder in your Unity 3D project to the one in this folder, which namely means that you should add this folder and other two packages as dependencies in your `Packages/manifest.json` file.
2. Place `VideoStreamingServerPackages` folder into your Unity 3D project's root folder in order to make the path described in `manifest.json` the same as in real path, otherwise you should change the path described in `manifest.json` file.
3. Use it as you want. The native C++ file is in `Native~` folder and you can even redevelop it yourself.
4. The main script is `VideoStreamingServer` and you can use it as a component.





<br><br><br>

<p align="right">Alexander Ezharjan</p><p align="right">9th Sep, 2020</p>







