#pragma once
#ifndef __TREEMANAGER_H__
#define __TREEMANAGER_H__

using namespace Colo

#ifdef __cplusplus
extern "C" {
#endif

#define APIEXPORT __declspec(dllexport)

int APIEXPORT Addition(int, int);
float APIEXPORT Clamp(float, float, float);
float APIEXPORT min(float a, float b);
float APIEXPORT max(float, float);
int APIEXPORT Test();

#ifdef __cplusplus
}
#endif
#endif